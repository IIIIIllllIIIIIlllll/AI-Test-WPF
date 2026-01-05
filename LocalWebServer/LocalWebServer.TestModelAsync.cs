using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using AI_Test.OpenAI;
using AI_Test.OpenAI.Models;
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

        try
        {
            var request = JsonSerializer.Deserialize<ChatCompletionRequest>(payloadJson);
            if (request?.Stream is not null)
            {
                streamRequested = request.Stream.Value;
            }

            if (string.IsNullOrWhiteSpace(modelForSave) && !string.IsNullOrWhiteSpace(request?.Model))
            {
                modelForSave = request.Model;
            }
        }
        catch
        {
        }

        var forwardUri = OpenAIChatCompletionsProxy.GetChatCompletionsUri(baseUri);

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
                    capturedAnswer = await OpenAIChatCompletionsProxy.ProxyStreamAndCaptureSseAnswerAsync(forwardResponse, context, context.RequestAborted);
                }
                else
                {
                    var responseText = await forwardResponse.Content.ReadAsStringAsync(context.RequestAborted);
                    await context.Response.WriteAsync(responseText, context.RequestAborted);
                    capturedAnswer = OpenAIChatCompletionsProxy.TryExtractChatCompletionContent(responseText);
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
}
