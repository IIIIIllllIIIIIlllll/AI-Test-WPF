using System.IO;
using Microsoft.AspNetCore.Http;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task GetConfigAsync(HttpContext context)
    {
        var filePath = GetConfigFilePath();
        await _configLock.WaitAsync(context.RequestAborted);
        try
        {
            if (!File.Exists(filePath))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"providers\":[],\"selectedProviderId\":null,\"selectedModel\":null}", context.RequestAborted);
                return;
            }

            context.Response.ContentType = "application/json; charset=utf-8";
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to read config. {ex.Message}");
        }
        finally
        {
            _configLock.Release();
        }
    }
}

