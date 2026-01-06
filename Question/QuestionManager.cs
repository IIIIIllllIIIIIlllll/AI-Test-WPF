using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Test.Question;

public sealed class QuestionManager
{
    public enum AttachmentAddStatus
    {
        Ok,
        InvalidInput,
        QuestionListNotFound,
        InvalidFormat,
        QuestionNotFound
    }

    public enum AttachmentRemoveStatus
    {
        Ok,
        InvalidInput,
        QuestionListNotFound,
        InvalidFormat,
        QuestionNotFound
    }

    private readonly SemaphoreSlim _lock;
    private readonly string _questionFilePath;

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

    public QuestionManager(string? questionFilePath = null, SemaphoreSlim? @lock = null)
    {
        _questionFilePath = string.IsNullOrWhiteSpace(questionFilePath) ? GetDefaultQuestionFilePath() : questionFilePath.Trim();
        _lock = @lock ?? new SemaphoreSlim(1, 1);
    }

    public string QuestionFilePath => _questionFilePath;

    public static string GetDefaultQuestionFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Test");
        return Path.Combine(dir, "questions.json");
    }

    public async Task<string> GetOrCreateQuestionsJsonAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_questionFilePath))
            {
                var dir = Path.GetDirectoryName(_questionFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                // 文件不存在，创建默认的‘问题’列表
                // 代办
                var json = JsonSerializer.Serialize(CreateDefaultPayload(), RelaxedIndentedJsonOptions);
                await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
                return json;
            }

            return await File.ReadAllTextAsync(_questionFilePath, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> AddQuestionAsync(string title, string content, string? answer = null, string? scoring = null, JsonArray? attachments = null, CancellationToken cancellationToken = default)
    {
        var newId = $"q_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        JsonArray attachmentsCopy;
        string? createdAttachmentsDirectory = null;
        try
        {
            (attachmentsCopy, createdAttachmentsDirectory) = await SaveAttachmentsAsync(_questionFilePath, newId, attachments, cancellationToken);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(createdAttachmentsDirectory))
            {
                try
                {
                    Directory.Delete(createdAttachmentsDirectory, true);
                }
                catch
                {
                }
            }

            throw;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_questionFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var (rootObject, dataArray) = await ReadOrCreateRootAsync(_questionFilePath, cancellationToken);

            var newQuestion = new JsonObject
            {
                ["id"] = newId,
                ["title"] = title,
                ["content"] = content,
                ["answer"] = answer ?? "",
                ["scoring"] = scoring ?? "",
                ["attachments"] = attachmentsCopy,
                ["answers"] = new JsonObject()
            };

            dataArray.Add(newQuestion);

            var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
            await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
            return newId;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(createdAttachmentsDirectory))
            {
                try
                {
                    Directory.Delete(createdAttachmentsDirectory, true);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdateQuestionAsync(string questionId, string title, string content, string? answer = null, string? scoring = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_questionFilePath))
            {
                return false;
            }

            JsonNode? existingRoot;
            using (var existingStream = new FileStream(_questionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                existingRoot = await JsonNode.ParseAsync(existingStream, cancellationToken: cancellationToken);
            }

            if (existingRoot is not JsonObject rootObject || rootObject["data"] is not JsonArray dataArray)
            {
                return false;
            }

            JsonObject? questionObject = null;
            foreach (var node in dataArray)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }

                var id = obj["id"]?.GetValue<string>()?.Trim();
                if (string.Equals(id, questionId, StringComparison.Ordinal))
                {
                    questionObject = obj;
                    break;
                }
            }

            if (questionObject is null)
            {
                return false;
            }

            questionObject["title"] = title;
            questionObject["content"] = content;
            questionObject["answer"] = answer ?? "";
            questionObject["scoring"] = scoring ?? "";

            var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
            await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool removed, bool deletedAttachments)> RemoveQuestionAsync(string questionId, CancellationToken cancellationToken = default)
    {
        var removed = false;
        var deletedAttachments = false;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_questionFilePath))
            {
                JsonNode? existingRoot;
                using (var existingStream = new FileStream(_questionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    existingRoot = await JsonNode.ParseAsync(existingStream, cancellationToken: cancellationToken);
                }

                if (existingRoot is JsonObject rootObject && rootObject["data"] is JsonArray dataArray)
                {
                    for (var i = dataArray.Count - 1; i >= 0; i--)
                    {
                        if (dataArray[i] is not JsonObject qObj)
                        {
                            continue;
                        }

                        var id = qObj["id"]?.GetValue<string>()?.Trim();
                        if (!string.Equals(id, questionId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        dataArray.RemoveAt(i);
                        removed = true;
                    }

                    if (removed)
                    {
                        var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
                        await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
                    }
                }
            }

            var rootDirectory = GetQuestionAttachmentsRootDirectory(_questionFilePath);
            var questionDirectory = Path.Combine(rootDirectory, questionId);
            if (Directory.Exists(questionDirectory))
            {
                Directory.Delete(questionDirectory, true);
                deletedAttachments = true;
            }

            return (removed, deletedAttachments);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(AttachmentRemoveStatus status, bool removedFromList, bool deletedFile)> RemoveAttachmentAsync(string questionId, string fileName, CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
        {
            return (AttachmentRemoveStatus.InvalidInput, false, false);
        }

        var removedFromList = false;
        var deletedFile = false;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_questionFilePath))
            {
                return (AttachmentRemoveStatus.QuestionListNotFound, false, false);
            }

            JsonNode? existingRoot;
            using (var existingStream = new FileStream(_questionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                existingRoot = await JsonNode.ParseAsync(existingStream, cancellationToken: cancellationToken);
            }

            if (existingRoot is not JsonObject rootObject || rootObject["data"] is not JsonArray dataArray)
            {
                return (AttachmentRemoveStatus.InvalidFormat, false, false);
            }

            JsonObject? questionObject = null;
            foreach (var node in dataArray)
            {
                if (node is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>()?.Trim();
                if (string.Equals(id, questionId, StringComparison.Ordinal))
                {
                    questionObject = obj;
                    break;
                }
            }

            if (questionObject is null)
            {
                return (AttachmentRemoveStatus.QuestionNotFound, false, false);
            }

            if (questionObject["attachments"] is JsonArray attachmentsArray)
            {
                for (var i = attachmentsArray.Count - 1; i >= 0; i--)
                {
                    var node = attachmentsArray[i];
                    if (node is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (string.Equals((s ?? "").Trim(), safeName, StringComparison.OrdinalIgnoreCase))
                        {
                            attachmentsArray.RemoveAt(i);
                            removedFromList = true;
                        }
                        continue;
                    }

                    if (node is JsonObject obj)
                    {
                        var n = GetOptionalString(obj, "fileName") ?? GetOptionalString(obj, "name");
                        if (string.Equals((n ?? "").Trim(), safeName, StringComparison.OrdinalIgnoreCase))
                        {
                            attachmentsArray.RemoveAt(i);
                            removedFromList = true;
                        }
                    }
                }
            }

            if (removedFromList)
            {
                var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
                await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
            }

            var rootDirectory = GetQuestionAttachmentsRootDirectory(_questionFilePath);
            var questionDirectory = Path.Combine(rootDirectory, questionId);
            var baseFull = Path.GetFullPath(questionDirectory);
            var candidateFull = Path.GetFullPath(Path.Combine(questionDirectory, safeName));

            if (candidateFull.StartsWith(baseFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidateFull))
            {
                File.Delete(candidateFull);
                deletedFile = true;
            }

            return (AttachmentRemoveStatus.Ok, removedFromList, deletedFile);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(AttachmentAddStatus status, string? fileName)> AddAttachmentFromStreamAsync(string questionId, string desiredFileName, Stream contentStream, CancellationToken cancellationToken = default)
    {
        if (!IsSafeQuestionId(questionId))
        {
            return (AttachmentAddStatus.InvalidInput, null);
        }

        var sanitized = SanitizeFileName(Path.GetFileName(desiredFileName));
        if (!TryGetSupportedContentType(sanitized, out _))
        {
            return (AttachmentAddStatus.InvalidInput, null);
        }

        string? createdFilePath = null;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_questionFilePath))
            {
                return (AttachmentAddStatus.QuestionListNotFound, null);
            }

            JsonNode? existingRoot;
            using (var existingStream = new FileStream(_questionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                existingRoot = await JsonNode.ParseAsync(existingStream, cancellationToken: cancellationToken);
            }

            if (existingRoot is not JsonObject rootObject || rootObject["data"] is not JsonArray dataArray)
            {
                return (AttachmentAddStatus.InvalidFormat, null);
            }

            JsonObject? questionObject = null;
            foreach (var node in dataArray)
            {
                if (node is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>()?.Trim();
                if (string.Equals(id, questionId, StringComparison.Ordinal))
                {
                    questionObject = obj;
                    break;
                }
            }

            if (questionObject is null)
            {
                return (AttachmentAddStatus.QuestionNotFound, null);
            }

            if (questionObject["attachments"] is not JsonArray attachmentsArray)
            {
                attachmentsArray = new JsonArray();
                questionObject["attachments"] = attachmentsArray;
            }

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in attachmentsArray)
            {
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    usedNames.Add(s.Trim());
                }
            }

            var rootDirectory = GetQuestionAttachmentsRootDirectory(_questionFilePath);
            var questionDirectory = Path.Combine(rootDirectory, questionId);
            Directory.CreateDirectory(questionDirectory);

            var safeName = EnsureUniqueFileName(questionDirectory, sanitized, usedNames);
            var targetPath = Path.Combine(questionDirectory, safeName);

            await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await contentStream.CopyToAsync(output, cancellationToken);
            }
            createdFilePath = targetPath;

            attachmentsArray.Add(safeName);

            var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
            await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
            return (AttachmentAddStatus.Ok, safeName);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(createdFilePath) && File.Exists(createdFilePath))
            {
                try
                {
                    File.Delete(createdFilePath);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SaveAnswerAsync(string questionId, string providerId, string model, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(questionId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_questionFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var (rootObject, dataArray) = await ReadOrCreateRootAsync(_questionFilePath, cancellationToken);

            JsonObject? questionObject = null;
            foreach (var node in dataArray)
            {
                if (node is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>()?.Trim();
                if (string.Equals(id, questionId, StringComparison.Ordinal))
                {
                    questionObject = obj;
                    break;
                }
            }

            if (questionObject is null)
            {
                return false;
            }

            if (questionObject["answers"] is not JsonObject answersObject)
            {
                answersObject = new JsonObject();
                questionObject["answers"] = answersObject;
            }

            if (answersObject[providerId] is not JsonObject providerObject)
            {
                providerObject = new JsonObject();
                answersObject[providerId] = providerObject;
            }

            providerObject[model] = content ?? "";

            var json = rootObject.ToJsonString(RelaxedIndentedJsonOptions);
            await File.WriteAllTextAsync(_questionFilePath, json, new UTF8Encoding(false), cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string GetQuestionAttachmentsRootDirectory(string questionFilePath)
    {
        var baseDir = Path.GetDirectoryName(questionFilePath) ?? "";
        return Path.Combine(baseDir, "question_attachments");
    }

    public static bool IsSafeQuestionId(string questionId)
    {
        if (string.IsNullOrWhiteSpace(questionId) || questionId.Length > 80)
        {
            return false;
        }

        foreach (var c in questionId)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static bool TryGetSupportedContentType(string fileName, out string contentType)
    {
        contentType = "";
        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();

        switch (ext)
        {
            case ".png":
                contentType = "image/png";
                return true;
            case ".jpg":
            case ".jpeg":
                contentType = "image/jpeg";
                return true;
            case ".gif":
                contentType = "image/gif";
                return true;
            case ".webp":
                contentType = "image/webp";
                return true;
            case ".bmp":
                contentType = "image/bmp";
                return true;
            case ".svg":
                contentType = "image/svg+xml";
                return true;
            case ".txt":
            case ".md":
            case ".log":
            case ".csv":
            case ".ini":
            case ".cfg":
            case ".conf":
            case ".config":
            case ".properties":
            case ".env":
            case ".toml":
            case ".yaml":
            case ".yml":
            case ".xml":
            case ".sql":
            case ".sh":
            case ".bash":
            case ".ps1":
            case ".bat":
            case ".cmd":
            case ".py":
            case ".js":
            case ".ts":
            case ".tsx":
            case ".jsx":
            case ".css":
            case ".scss":
            case ".less":
            case ".html":
            case ".htm":
            case ".cs":
            case ".csproj":
            case ".sln":
            case ".cpp":
            case ".c":
            case ".h":
            case ".hpp":
            case ".java":
            case ".kt":
            case ".go":
            case ".rs":
            case ".php":
            case ".rb":
            case ".swift":
            case ".dart":
                contentType = "text/plain; charset=utf-8";
                return true;
            case ".json":
                contentType = "application/json; charset=utf-8";
                return true;
            default:
                contentType = "";
                return false;
        }
    }

    public static string SanitizeFileName(string fileName)
    {
        var name = (fileName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "attachment";
        }

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            name = "attachment";
        }

        return name.Length > 150 ? name[..150] : name;
    }

    private static object CreateDefaultPayload()
    {
        return new object();
    }

    private static async Task<(JsonObject root, JsonArray data)> ReadOrCreateRootAsync(string filePath, CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
        {
            JsonNode? existingRoot;
            using (var existingStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                existingRoot = await JsonNode.ParseAsync(existingStream, cancellationToken: cancellationToken);
            }

            if (existingRoot is JsonObject existingObj && existingObj["data"] is JsonArray arr)
            {
                return (existingObj, arr);
            }
        }

        var rootObject = new JsonObject();
        var dataArray = new JsonArray();
        rootObject["data"] = dataArray;
        return (rootObject, dataArray);
    }

    private static async Task<(JsonArray attachments, string? createdDirectory)> SaveAttachmentsAsync(
        string questionFilePath,
        string questionId,
        JsonArray? incomingAttachments,
        CancellationToken cancellationToken)
    {
        var result = new JsonArray();

        if (incomingAttachments is null || incomingAttachments.Count == 0)
        {
            return (result, null);
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDirectory = GetQuestionAttachmentsRootDirectory(questionFilePath);
        string? createdDirectory = null;

        foreach (var node in incomingAttachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is JsonValue value && value.TryGetValue<string>(out var rawNameText))
            {
                var safeName = MakeUniqueName(SanitizeFileName(rawNameText), usedNames);
                result.Add(safeName);
                continue;
            }

            if (node is not JsonObject obj)
            {
                continue;
            }

            var fileName = GetOptionalString(obj, "fileName") ?? GetOptionalString(obj, "name");
            var base64 = GetOptionalString(obj, "base64");

            if (string.IsNullOrWhiteSpace(fileName) && string.IsNullOrWhiteSpace(base64))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(base64))
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    throw new InvalidDataException("Attachment missing fileName.");
                }

                if (!TryDecodeBase64(base64, out var bytes))
                {
                    throw new InvalidDataException($"Invalid base64 for attachment: {fileName}");
                }

                createdDirectory ??= Path.Combine(rootDirectory, questionId);
                Directory.CreateDirectory(createdDirectory);

                var safeName = EnsureUniqueFileName(createdDirectory, SanitizeFileName(fileName), usedNames);
                var targetPath = Path.Combine(createdDirectory, safeName);
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
                result.Add(safeName);
                continue;
            }

            var safeListedName = MakeUniqueName(SanitizeFileName(fileName ?? ""), usedNames);
            result.Add(safeListedName);
        }

        return (result, createdDirectory);
    }

    private static string? GetOptionalString(JsonObject obj, string propertyName)
    {
        if (obj.TryGetPropertyValue(propertyName, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            return text?.Trim();
        }

        return null;
    }

    private static bool TryDecodeBase64(string base64Text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(base64Text))
        {
            return false;
        }

        var s = base64Text.Trim();
        var comma = s.IndexOf(',');
        if (comma >= 0 && s[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            s = s[(comma + 1)..];
        }

        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static string MakeUniqueName(string desiredName, HashSet<string> usedNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(desiredName);
        var ext = Path.GetExtension(desiredName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "attachment";
        }

        var name = $"{baseName}{ext}";
        var i = 1;
        while (usedNames.Contains(name))
        {
            name = $"{baseName}_{i}{ext}";
            i++;
        }

        usedNames.Add(name);
        return name;
    }

    private static string EnsureUniqueFileName(string directory, string desiredName, HashSet<string> usedNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(desiredName);
        var ext = Path.GetExtension(desiredName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "attachment";
        }

        var name = $"{baseName}{ext}";
        var i = 1;
        while (usedNames.Contains(name) || File.Exists(Path.Combine(directory, name)))
        {
            name = $"{baseName}_{i}{ext}";
            i++;
        }

        usedNames.Add(name);
        return name;
    }
}
