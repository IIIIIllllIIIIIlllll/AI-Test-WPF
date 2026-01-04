using System.IO;
using Microsoft.AspNetCore.Http;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer
{
    private async Task FallbackAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_resourcesDirectoryFullName))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var requestPath = context.Request.Path.Value?.TrimStart('/') ?? "";
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            context.Response.Redirect("/");
            return;
        }

        var candidatePath = Path.Combine(_resourcesDirectoryFullName, requestPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidatePath))
        {
            await context.Response.SendFileAsync(candidatePath, _serverCancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}

