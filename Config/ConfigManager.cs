using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Test.Config;

public sealed class ConfigManager
{
    private readonly SemaphoreSlim _lock;
    private readonly string _configFilePath;

    private static readonly JsonSerializerOptions RelaxedIndentedJsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static string DefaultConfigJson => "{\"providers\":[],\"selectedProviderId\":null,\"selectedModel\":null}";

    public ConfigManager(string? configFilePath = null, SemaphoreSlim? @lock = null)
    {
        _configFilePath = string.IsNullOrWhiteSpace(configFilePath) ? GetDefaultConfigFilePath() : configFilePath.Trim();
        _lock = @lock ?? new SemaphoreSlim(1, 1);
    }

    public string ConfigFilePath => _configFilePath;

    public static string GetDefaultConfigFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Test");
        return Path.Combine(dir, "config.json");
    }

    public async Task<string> GetConfigJsonAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_configFilePath))
            {
                return DefaultConfigJson;
            }

            return await File.ReadAllTextAsync(_configFilePath, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetOrCreateConfigJsonAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_configFilePath))
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var defaultJson = JsonSerializer.Serialize(CreateDefaultConfig(), RelaxedIndentedJsonOptions);
                await File.WriteAllTextAsync(_configFilePath, defaultJson, new UTF8Encoding(false), cancellationToken);
                return defaultJson;
            }

            return await File.ReadAllTextAsync(_configFilePath, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveConfigAsync(JsonNode configNode, CancellationToken cancellationToken = default)
    {
        if (configNode is null)
        {
            throw new ArgumentNullException(nameof(configNode));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = configNode.ToJsonString(RelaxedIndentedJsonOptions);
            await File.WriteAllTextAsync(_configFilePath, json, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static object CreateDefaultConfig()
    {
        return new
        {
            providers = Array.Empty<object>(),
            selectedProviderId = (string?)null,
            selectedModel = (string?)null
        };
    }
}
