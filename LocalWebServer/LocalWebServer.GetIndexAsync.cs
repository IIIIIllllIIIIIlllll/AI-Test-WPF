using System.IO;
using Microsoft.AspNetCore.Http;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task GetIndexAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_resourcesDirectoryFullName))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(_resourcesDirectoryFullName, "index.html"), _serverCancellationToken);
    }
}

