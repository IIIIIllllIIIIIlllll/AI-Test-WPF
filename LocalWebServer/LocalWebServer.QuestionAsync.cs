using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AI_Test.Question;

namespace AI_Test.LocalWebServer
{
    public sealed partial class LocalWebServer
    {

        /// <summary>
        /// 返回待测试的问题。
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task ListQuestionAsync(HttpContext context)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            try
            {
                var manager = GetQuestionManager();
                var json = await manager.GetOrCreateQuestionsJsonAsync(context.RequestAborted);
                await context.Response.WriteAsync(json, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to read questions. {ex.Message}");
            }
        }


        /// <summary>
        /// 添加待测试的问题。
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task AddQuestionAsync(HttpContext context)
        {
            if (!context.Request.HasJsonContentType())
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
                return;
            }

            var bodyObject = await ReadJsonObjectBodyAsync(context);
            if (bodyObject is null)
            {
                return;
            }

            var title = bodyObject["title"]?.GetValue<string>()?.Trim();
            var content = bodyObject["content"]?.GetValue<string>()?.Trim();
            var answer = bodyObject["answer"]?.GetValue<string>()?.Trim();
            var scoring = bodyObject["scoring"]?.GetValue<string>()?.Trim();
            var attachments = bodyObject["attachments"] as JsonArray;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required fields: title, content.");
                return;
            }

            try
            {
                var manager = GetQuestionManager();
                var newId = await manager.AddQuestionAsync(title, content, answer, scoring, attachments, context.RequestAborted);

                context.Response.ContentType = "application/json; charset=utf-8";
                var responsePayload = new JsonObject
                {
                    ["ok"] = true,
                    ["id"] = newId
                };
                await context.Response.WriteAsync(responsePayload.ToJsonString(RelaxedJsonOptions), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidDataException ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to save question. {ex.Message}");
            }
        }


        /// <summary>
        /// 移除一个‘问题’
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task RemoveQuestionAsync(HttpContext context)
        {
            if (!context.Request.HasJsonContentType())
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
                return;
            }

            var bodyObject = await ReadJsonObjectBodyAsync(context);
            if (bodyObject is null)
            {
                return;
            }

            var questionId = bodyObject["id"]?.GetValue<string>()?.Trim()
                             ?? bodyObject["questionId"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: id.");
                return;
            }

            if (!QuestionManager.IsSafeQuestionId(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid questionId.");
                return;
            }

            try
            {
                var manager = GetQuestionManager();
                var result = await manager.RemoveQuestionAsync(questionId, context.RequestAborted);

                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = new JsonObject
                {
                    ["ok"] = true,
                    ["removed"] = result.removed,
                    ["deletedAttachments"] = result.deletedAttachments
                };
                await context.Response.WriteAsync(payload.ToJsonString(RelaxedJsonOptions), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to remove question. {ex.Message}");
            }
        }


        /// <summary>
        /// 给指定的‘问题’，添加一个附件
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task AddQuestionFileAsync(HttpContext context)
        {
            if (!context.Request.HasFormContentType)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be multipart/form-data.");
                return;
            }

            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, $"Invalid form data. {ex.Message}");
                return;
            }

            var questionId = (form["id"].ToString().Trim() ?? "")
                             .Trim();
            if (string.IsNullOrWhiteSpace(questionId))
            {
                questionId = (form["questionId"].ToString().Trim() ?? "").Trim();
            }

            if (string.IsNullOrWhiteSpace(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required field: id.");
                return;
            }

            if (!QuestionManager.IsSafeQuestionId(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid questionId.");
                return;
            }

            var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null || file.Length <= 0)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required file.");
                return;
            }

            var fileNameFromForm = form["fileName"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(fileNameFromForm))
            {
                fileNameFromForm = form["name"].ToString().Trim();
            }

            var desiredName = (fileNameFromForm ?? "").Trim();
            if (string.IsNullOrWhiteSpace(desiredName))
            {
                desiredName = (file.FileName ?? "").Trim();
            }
            desiredName = QuestionManager.SanitizeFileName(Path.GetFileName(desiredName));

            if (!QuestionManager.TryGetSupportedContentType(desiredName, out _))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported file type.");
                return;
            }
            try
            {
                var manager = GetQuestionManager();
                await using var input = file.OpenReadStream();
                var result = await manager.AddAttachmentFromStreamAsync(questionId, desiredName, input, context.RequestAborted);

                if (result.status == QuestionManager.AttachmentAddStatus.QuestionListNotFound)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Question list not found.");
                    return;
                }

                if (result.status == QuestionManager.AttachmentAddStatus.QuestionNotFound)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Question not found.");
                    return;
                }

                if (result.status == QuestionManager.AttachmentAddStatus.InvalidFormat)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, "Invalid questions.json format.");
                    return;
                }

                if (result.status != QuestionManager.AttachmentAddStatus.Ok || string.IsNullOrWhiteSpace(result.fileName))
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid request.");
                    return;
                }

                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = new JsonObject
                {
                    ["ok"] = true,
                    ["fileName"] = result.fileName
                };
                await context.Response.WriteAsync(payload.ToJsonString(RelaxedJsonOptions), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to add attachment. {ex.Message}");
            }
        }

        /// <summary>
        /// 给指定的‘问题’，移除一个附件
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task RemoveQuestionFileAsync(HttpContext context)
        {
            if (!context.Request.HasJsonContentType())
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
                return;
            }

            var bodyObject = await ReadJsonObjectBodyAsync(context);
            if (bodyObject is null)
            {
                return;
            }

            var questionId = bodyObject["id"]?.GetValue<string>()?.Trim()
                             ?? bodyObject["questionId"]?.GetValue<string>()?.Trim();
            var fileName = bodyObject["fileName"]?.GetValue<string>()?.Trim()
                           ?? bodyObject["name"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(questionId) || string.IsNullOrWhiteSpace(fileName))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required fields: id, fileName.");
                return;
            }

            if (!QuestionManager.IsSafeQuestionId(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid questionId.");
                return;
            }

            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid fileName.");
                return;
            }

            try
            {
                var manager = GetQuestionManager();
                var result = await manager.RemoveAttachmentAsync(questionId, safeName, context.RequestAborted);

                if (result.status == QuestionManager.AttachmentRemoveStatus.QuestionListNotFound)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Question list not found.");
                    return;
                }

                if (result.status == QuestionManager.AttachmentRemoveStatus.QuestionNotFound)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "Question not found.");
                    return;
                }

                if (result.status == QuestionManager.AttachmentRemoveStatus.InvalidFormat)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, "Invalid questions.json format.");
                    return;
                }

                if (result.status != QuestionManager.AttachmentRemoveStatus.Ok)
                {
                    await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid request.");
                    return;
                }

                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = new JsonObject
                {
                    ["ok"] = true,
                    ["removed"] = result.removedFromList,
                    ["deleted"] = result.deletedFile
                };
                await context.Response.WriteAsync(payload.ToJsonString(RelaxedJsonOptions), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to remove attachment. {ex.Message}");
            }
        }


        private async Task GetQuestionFileAsync(HttpContext context)
        {
            var questionId = context.Request.Query["questionId"].ToString().Trim();
            var fileName = context.Request.Query["fileName"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(questionId) || string.IsNullOrWhiteSpace(fileName))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required query: questionId, fileName.");
                return;
            }

            if (!QuestionManager.IsSafeQuestionId(questionId))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid questionId.");
                return;
            }

            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid fileName.");
                return;
            }

            var questionFilePath = GetQuestionFilePath();
            var rootDirectory = QuestionManager.GetQuestionAttachmentsRootDirectory(questionFilePath);
            var questionDirectory = Path.Combine(rootDirectory, questionId);
            var baseFull = Path.GetFullPath(questionDirectory);
            var candidateFull = Path.GetFullPath(Path.Combine(questionDirectory, safeName));

            if (!candidateFull.StartsWith(baseFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid fileName.");
                return;
            }

            var contentType = QuestionManager.TryGetSupportedContentType(safeName, out var resolvedContentType)
                ? resolvedContentType
                : null;

            if (string.IsNullOrWhiteSpace(contentType))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported file type.");
                return;
            }

            if (!File.Exists(candidateFull))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status404NotFound, "File not found.");
                return;
            }

            context.Response.ContentType = contentType;
            context.Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
            await context.Response.SendFileAsync(candidateFull, context.RequestAborted);
        }

        /// <summary>
        /// 保存问题的答案。
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task SaveQuestionAnswerAsync(HttpContext context)
        {
            if (!context.Request.HasJsonContentType())
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
                return;
            }

            var bodyObject = await ReadJsonObjectBodyAsync(context);
            if (bodyObject is null)
            {
                return;
            }

            var questionId = bodyObject["questionId"]?.GetValue<string>()?.Trim();
            var providerId = bodyObject["providerId"]?.GetValue<string>()?.Trim();
            var model = bodyObject["model"]?.GetValue<string>()?.Trim();
            var content = bodyObject["content"]?.GetValue<string>() ?? "";

            if (string.IsNullOrWhiteSpace(questionId)
                || string.IsNullOrWhiteSpace(providerId)
                || string.IsNullOrWhiteSpace(model))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Missing required fields: questionId, providerId, model.");
                return;
            }

            try
            {
                var manager = GetQuestionManager();
                var saved = await manager.SaveAnswerAsync(questionId, providerId, model, content, context.RequestAborted);
                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = new JsonObject
                {
                    ["ok"] = saved,
                };
                await context.Response.WriteAsync(payload.ToJsonString(RelaxedJsonOptions), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status500InternalServerError, $"Failed to save answer. {ex.Message}");
            }
        }

        private static async Task<JsonObject?> ReadJsonObjectBodyAsync(HttpContext context)
        {
            JsonNode? rootNode;
            try
            {
                rootNode = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid JSON body.");
                return null;
            }

            if (rootNode is not JsonObject bodyObject)
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, "JSON body must be an object.");
                return null;
            }

            return bodyObject;
        }

    }
}
