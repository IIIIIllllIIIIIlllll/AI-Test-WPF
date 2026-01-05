using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using AI_Test.Question;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task TestModelAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
            return;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid JSON body.");
            return;
        }

        if (rootNode is not JsonObject bodyObject)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "JSON body must be an object.");
            return;
        }

        var questionId = bodyObject["questionId"]?.GetValue<string>()?.Trim();
        var providerId = bodyObject["providerId"]?.GetValue<string>()?.Trim();
        var modelForSave = bodyObject["model"]?.GetValue<string>()?.Trim();

        string? host = null;
        if (bodyObject.TryGetPropertyValue("host", out var hostNode)
            && hostNode is JsonValue hostValue
            && hostValue.TryGetValue<string>(out var hostValueText))
        {
            host = hostValueText?.Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: host.");
            return;
        }

        if (!TryNormalizeHost(host, out var baseUri))
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid host. Use an absolute http/https URL, e.g. https://api.openai.com or http://127.0.0.1:8080.");
            return;
        }

        var streamRequested = false;
        if (bodyObject.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var streamBool))
        {
            streamRequested = streamBool;
        }

        bodyObject.Remove("host");
        bodyObject.Remove("questionId");
        bodyObject.Remove("providerId");
        var payloadJson = bodyObject.ToJsonString();

        var forwardUri = new Uri(baseUri, "v1/chat/completions");

        HttpResponseMessage forwardResponse;
        try
        {
            forwardResponse = await ForwardPostJsonAsync(context, forwardUri, payloadJson);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status502BadGateway, $"Failed to reach upstream host. {ex.Message}");
            return;
        }

        using (forwardResponse)
        {
            context.Response.StatusCode = (int)forwardResponse.StatusCode;

            CopyResponseHeaders(forwardResponse, context.Response);

            if (forwardResponse.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType = forwardResponse.Content.Headers.ContentType.ToString();
            }

            string? capturedAnswer = null;
            try
            {
                if (streamRequested)
                {
                    capturedAnswer = await ProxyStreamAndCaptureSseAnswerAsync(forwardResponse, context);
                }
                else
                {
                    var responseText = await forwardResponse.Content.ReadAsStringAsync(context.RequestAborted);
                    await context.Response.WriteAsync(responseText, context.RequestAborted);
                    capturedAnswer = TryExtractChatCompletionContent(responseText);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                if (streamRequested)
                {
                    capturedAnswer ??= context.Items.TryGetValue("capturedAnswer", out var value) ? value as string : null;
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(questionId)
                    && !string.IsNullOrWhiteSpace(providerId)
                    && !string.IsNullOrWhiteSpace(modelForSave)
                    && !string.IsNullOrEmpty(capturedAnswer))
                {
                    try
                    {
                        var manager = GetQuestionManager();
                        await manager.SaveAnswerAsync(questionId!, providerId!, modelForSave!, capturedAnswer!, _serverCancellationToken);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private async Task<string?> ProxyStreamAndCaptureSseAnswerAsync(HttpResponseMessage forwardResponse, HttpContext context)
    {
        var responseStream = await forwardResponse.Content.ReadAsStreamAsync(context.RequestAborted);

        var decoder = Encoding.UTF8.GetDecoder();
        var bytesBuffer = new byte[16 * 1024];
        var charsBuffer = new char[16 * 1024];

        var pendingText = "";
        var capturedContent = new StringBuilder();
        var capturedReasoning = new StringBuilder();

        while (true)
        {
            var read = await responseStream.ReadAsync(bytesBuffer.AsMemory(0, bytesBuffer.Length), context.RequestAborted);
            if (read <= 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(bytesBuffer.AsMemory(0, read), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            var chars = decoder.GetChars(bytesBuffer, 0, read, charsBuffer, 0, flush: false);
            if (chars <= 0) continue;

            pendingText += new string(charsBuffer, 0, chars);

            while (true)
            {
                var newlineIndex = pendingText.IndexOf('\n');
                if (newlineIndex < 0) break;

                var line = pendingText.Substring(0, newlineIndex);
                pendingText = pendingText.Substring(newlineIndex + 1);

                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = trimmed[5..].Trim();
                if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.Ordinal)) continue;

                try
                {
                    var json = JsonNode.Parse(data);
                    var deltaContent = json?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(deltaContent))
                    {
                        capturedContent.Append(deltaContent);
                    }

                    var deltaReasoning = json?["choices"]?[0]?["delta"]?["reasoning_content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(deltaReasoning))
                    {
                        capturedReasoning.Append(deltaReasoning);
                    }
                }
                catch
                {
                }
            }

            context.Items["capturedAnswer"] = BuildCombinedAnswer(capturedReasoning.ToString(), capturedContent.ToString());
        }

        var combined = BuildCombinedAnswer(capturedReasoning.ToString(), capturedContent.ToString());
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static string? TryExtractChatCompletionContent(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            var json = JsonNode.Parse(responseJson);
            var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            var reasoning = json?["choices"]?[0]?["message"]?["reasoning_content"]?.GetValue<string>();
            var combined = BuildCombinedAnswer(reasoning, content);
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCombinedAnswer(string? reasoningContent, string? content)
    {
        var reasoning = reasoningContent ?? "";
        var answer = content ?? "";

        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return answer;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return $"<reasoning_content>\n{reasoning}\n</reasoning_content>";
        }

        return $"<reasoning_content>\n{reasoning}\n</reasoning_content>\n\n{answer}";
    }
}
