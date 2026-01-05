using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using AI_Test.OpenAI.Models;

namespace AI_Test.OpenAI;

internal static class OpenAIChatCompletionsProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Uri GetChatCompletionsUri(Uri baseUri) => new(baseUri, "v1/chat/completions");

    public static async Task<string?> ProxyStreamAndCaptureSseAnswerAsync(HttpResponseMessage forwardResponse, HttpContext context, CancellationToken cancellationToken)
    {
        var responseStream = await forwardResponse.Content.ReadAsStreamAsync(cancellationToken);

        var decoder = Encoding.UTF8.GetDecoder();
        var bytesBuffer = new byte[16 * 1024];
        var charsBuffer = new char[16 * 1024];

        var pendingText = "";
        var capturedContent = new StringBuilder();
        var capturedReasoning = new StringBuilder();

        while (true)
        {
            var read = await responseStream.ReadAsync(bytesBuffer.AsMemory(0, bytesBuffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(bytesBuffer.AsMemory(0, read), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            var chars = decoder.GetChars(bytesBuffer, 0, read, charsBuffer, 0, flush: false);
            if (chars <= 0)
            {
                continue;
            }

            pendingText += new string(charsBuffer, 0, chars);

            while (true)
            {
                var newlineIndex = pendingText.IndexOf('\n');
                if (newlineIndex < 0)
                {
                    break;
                }

                var line = pendingText.Substring(0, newlineIndex);
                pendingText = pendingText.Substring(newlineIndex + 1);

                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = trimmed[5..].Trim();
                if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var chunk = JsonSerializer.Deserialize<ChatCompletionChunkResponse>(data, JsonOptions);
                    var delta = chunk?.Choices is { Count: > 0 } ? chunk.Choices[0].Delta : null;
                    if (!string.IsNullOrEmpty(delta?.Content))
                    {
                        capturedContent.Append(delta.Content);
                    }

                    if (!string.IsNullOrEmpty(delta?.ReasoningContent))
                    {
                        capturedReasoning.Append(delta.ReasoningContent);
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

    public static string? TryExtractChatCompletionContent(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, JsonOptions);
            var message = response?.Choices is { Count: > 0 } ? response.Choices[0].Message : null;
            var combined = BuildCombinedAnswer(message?.ReasoningContent, message?.Content);
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
