using System.IO;
using Microsoft.AspNetCore.Http;
using AI_Test.Config;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task GetConfigAsync(HttpContext context)
    {
        try
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            var manager = new ConfigManager(GetConfigFilePath(), _configLock);
            var json = await manager.GetConfigJsonAsync(context.RequestAborted);
            await context.Response.WriteAsync(json, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to read config. {ex.Message}");
        }
    }
}
