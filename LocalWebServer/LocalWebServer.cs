using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using AI_Test.Question;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AI_Test.LocalWebServer;

public sealed partial class LocalWebServer : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _proxyHttpClient;
    private readonly SemaphoreSlim _configLock = new SemaphoreSlim(1, 1);
    private QuestionManager? _questionManager;
    private string? _resourcesDirectoryFullName;
    private CancellationToken _serverCancellationToken;

    private static readonly JsonSerializerOptions RelaxedJsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSerializerOptions RelaxedIndentedJsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public Uri BaseUri { get; private set; } = new Uri("http://127.0.0.1/");

    public async Task StartAsync(string resourcesDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var port = GetAvailablePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LocalWebServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        _proxyHttpClient ??= CreateProxyHttpClient();

        var app = builder.Build();

        var resourcesDirectory = new DirectoryInfo(resourcesDirectoryPath);
        if (!resourcesDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"Resources directory not found: {resourcesDirectory.FullName}");
        }

        _resourcesDirectoryFullName = resourcesDirectory.FullName;
        _serverCancellationToken = cancellationToken;
        _questionManager = new QuestionManager(GetQuestionFilePath());

        var fileProvider = new PhysicalFileProvider(resourcesDirectory.FullName);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = "",
        });

        app.MapGet("/", GetIndexAsync);
        app.MapGet("/api/config/get", GetConfigAsync);
        app.MapPost("/api/config/set", SetConfigAsync);
        app.MapPost("/api/model/list", ListModelsAsync);
        app.MapPost("/api/model/test", TestModelAsync);

        app.MapGet("/api/question/list", ListQuestionAsync);
        app.MapPost("/api/question/list", ListQuestionAsync);
        app.MapPost("/api/question/add", AddQuestionAsync);
        app.MapPost("/api/question/remove", RemoveQuestionAsync);
        app.MapGet("/api/question/file/get", GetQuestionFileAsync);
        app.MapPost("/api/question/answer/save", SaveQuestionAnswerAsync);
        app.MapPost("/api/question/file/add", AddQuestionFileAsync);
        app.MapPost("/api/question/file/remove", RemoveQuestionFileAsync);

        app.MapFallback(FallbackAsync);

        _app = app;
        await app.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;

        _proxyHttpClient?.Dispose();
        _proxyHttpClient = null;
        _resourcesDirectoryFullName = null;
        _serverCancellationToken = default;
        _questionManager = null;
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static HttpClient CreateProxyHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        return httpClient;
    }

    private async Task<HttpResponseMessage> ForwardPostJsonAsync(HttpContext context, Uri forwardUri, string payloadJson)
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
            if (ShouldSkipRequestHeader(header.Key))
            {
                continue;
            }

            if (!forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                forwardRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return await _proxyHttpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }

    private async Task<HttpResponseMessage> ForwardGetAsync(HttpContext context, Uri forwardUri)
    {
        if (_proxyHttpClient is null)
        {
            throw new InvalidOperationException("Proxy HttpClient not initialized.");
        }

        var forwardRequest = new HttpRequestMessage(HttpMethod.Get, forwardUri);

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key))
            {
                continue;
            }

            forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        return await _proxyHttpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }

    private static bool ShouldSkipRequestHeader(string headerName)
    {
        return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key))
            {
                continue;
            }

            destination.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in source.Content.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key))
            {
                continue;
            }

            destination.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static bool ShouldSkipResponseHeader(string headerName)
    {
        return headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
               || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeHost(string host, out Uri normalizedBaseUri)
    {
        normalizedBaseUri = null!;

        var candidate = host.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        if (!baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new UriBuilder(baseUri)
        {
            Query = "",
            Fragment = "",
        };

        var path = builder.Path;
        if (!path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path = $"{path}/";
        }

        normalizedBaseUri = builder.Uri;
        return true;
    }

    private static string GetConfigFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Test");
        return Path.Combine(dir, "config.json");
    }

    private static string GetQuestionFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Test");
        return Path.Combine(dir, "questions.json");
    }

    private QuestionManager GetQuestionManager()
    {
        return _questionManager ?? throw new InvalidOperationException("QuestionManager not initialized.");
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.Serialize(new { error = message }, RelaxedJsonOptions);
        await context.Response.WriteAsync(payload, context.RequestAborted);
    }
}
