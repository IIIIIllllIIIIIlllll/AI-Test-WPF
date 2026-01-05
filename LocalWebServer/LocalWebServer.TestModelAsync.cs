using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using AI_Test.Config;
using AI_Test.OpenAI;
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

        if (string.IsNullOrWhiteSpace(questionId))
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: questionId.");
            return;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: providerId.");
            return;
        }

        if (string.IsNullOrWhiteSpace(modelForSave))
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: model.");
            return;
        }

        var streamRequested = false;
        if (bodyObject.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var streamBool))
        {
            streamRequested = streamBool;
        }

        string? apiKey;
        Uri baseUri;
        try
        {
            var configManager = new ConfigManager(GetConfigFilePath(), _configLock);
            var configJson = await configManager.GetOrCreateConfigJsonAsync(context.RequestAborted);
            var configNode = JsonNode.Parse(configJson) as JsonObject;
            var providers = configNode?["providers"] as JsonArray;

            JsonObject? provider = null;
            if (providers is not null)
            {
                foreach (var p in providers)
                {
                    if (p is not JsonObject pObj)
                    {
                        continue;
                    }

                    var id = pObj["id"]?.GetValue<string>()?.Trim();
                    if (!string.Equals(id, providerId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    provider = pObj;
                    break;
                }
            }

            if (provider is null)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Provider not found in config.");
                return;
            }

            var host = provider?["host"]?.GetValue<string>()?.Trim();
            apiKey = provider?["apiKey"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(host))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing provider host in config.");
                return;
            }

            if (!TryNormalizeHost(host, out baseUri))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid provider host. Use an absolute http/https URL, e.g. https://api.openai.com or http://127.0.0.1:8080.");
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing provider apiKey in config.");
                return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (JsonException)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, "Invalid config.json format.");
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to load config. {ex.Message}");
            return;
        }

        string? questionContent;
        JsonArray? questionAttachments;
        try
        {
            var manager = GetQuestionManager();
            var questionsJson = await manager.GetOrCreateQuestionsJsonAsync(context.RequestAborted);
            var questionsNode = JsonNode.Parse(questionsJson) as JsonObject;
            var dataArray = questionsNode?["data"] as JsonArray;

            questionContent = null;
            questionAttachments = null;
            if (dataArray is not null)
            {
                foreach (var item in dataArray)
                {
                    if (item is not JsonObject qObj)
                    {
                        continue;
                    }

                    var id = qObj["id"]?.GetValue<string>()?.Trim();
                    if (!string.Equals(id, questionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    questionContent = qObj["content"]?.GetValue<string>();
                    questionAttachments = qObj["attachments"] as JsonArray;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (JsonException)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, "Invalid questions.json format.");
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to load question. {ex.Message}");
            return;
        }

        if (questionContent is null)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Question not found.");
            return;
        }

        var contentParts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = questionContent
            }
        };

        if (questionAttachments is not null && questionAttachments.Count > 0)
        {
            var questionFilePath = GetQuestionFilePath();
            var attachmentsRoot = QuestionManager.GetQuestionAttachmentsRootDirectory(questionFilePath);
            var questionDirectory = Path.Combine(attachmentsRoot, questionId);
            var baseFull = Path.GetFullPath(questionDirectory);

            foreach (var node in questionAttachments)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var rawName) || string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var safeName = Path.GetFileName(rawName.Trim());
                if (!string.Equals(safeName, rawName.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateFull = Path.GetFullPath(Path.Combine(questionDirectory, safeName));
                if (!candidateFull.StartsWith(baseFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var contentType = QuestionManager.TryGetSupportedContentType(safeName, out var resolvedContentType)
                    ? resolvedContentType
                    : null;

                if (string.IsNullOrWhiteSpace(contentType) || !File.Exists(candidateFull))
                {
                    continue;
                }

                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(candidateFull, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        continue;
                    }

                    var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
                    contentParts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = dataUrl
                        }
                    });
                    continue;
                }

                if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    string text;
                    try
                    {
                        text = await File.ReadAllTextAsync(candidateFull, Encoding.UTF8, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        continue;
                    }

                    if (text.Length > 20000)
                    {
                        text = text[..20000];
                    }

                    contentParts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"[附件: {safeName}]\n{text}"
                    });
                }
            }
        }

        var payloadObject = new JsonObject
        {
            ["model"] = modelForSave,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = contentParts
                }
            },
            ["stream"] = streamRequested
        };

        var payloadJson = payloadObject.ToJsonString(RelaxedJsonOptions);

        var forwardUri = OpenAIChatCompletionsProxy.GetChatCompletionsUri(baseUri);

        HttpResponseMessage forwardResponse;
        try
        {
            forwardResponse = await ForwardPostJsonWithBearerAsync(context, forwardUri, payloadJson, apiKey!);
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

    private async Task<HttpResponseMessage> ForwardPostJsonWithBearerAsync(HttpContext context, Uri forwardUri, string payloadJson, string apiKey)
    {
        if (_proxyHttpClient is null)
        {
            throw new InvalidOperationException("Proxy HttpClient not initialized.");
        }

        var forwardRequest = new HttpRequestMessage(HttpMethod.Post, forwardUri)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                forwardRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        forwardRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        return await _proxyHttpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }
}
