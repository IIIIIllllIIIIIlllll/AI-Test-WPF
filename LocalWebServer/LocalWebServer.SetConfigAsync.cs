using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using AI_Test.Config;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task SetConfigAsync(HttpContext context)
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

        if (rootNode is null)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing JSON body.");
            return;
        }

        try
        {
            var manager = new ConfigManager(GetConfigFilePath(), _configLock);
            await manager.SaveConfigAsync(rootNode, context.RequestAborted);

            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"ok\":true}", context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to save config. {ex.Message}");
        }
    }
}
