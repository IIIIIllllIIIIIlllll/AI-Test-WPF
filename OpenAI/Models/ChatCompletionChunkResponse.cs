using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AI_Test.OpenAI.Models
{
    public class ChatCompletionChunkResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatCompletionChunkChoice> Choices { get; set; } = new List<ChatCompletionChunkChoice>();
    }

    public class ChatCompletionChunkChoice
    {
        [JsonPropertyName("delta")]
        public ChatCompletionChunkDelta Delta { get; set; } = new ChatCompletionChunkDelta();
    }

    public class ChatCompletionChunkDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
    }
}

