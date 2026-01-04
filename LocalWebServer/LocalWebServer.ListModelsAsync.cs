using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task ListModelsAsync(HttpContext context)
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

        var forwardUri = new Uri(baseUri, "v1/models");

        HttpResponseMessage forwardResponse;
        try
        {
            forwardResponse = await ForwardGetAsync(context, forwardUri);
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

            await forwardResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }
}

