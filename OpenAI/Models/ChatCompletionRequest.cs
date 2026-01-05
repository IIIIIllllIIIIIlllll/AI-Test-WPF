using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AI_Test.OpenAI.Models
{
    public class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "gpt-3.5-turbo";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        [JsonPropertyName("n")]
        public int? N { get; set; } = 1;

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; } = 1;

        [JsonPropertyName("top_p")]
        public double? TopP { get; set; } = 1;

        [JsonPropertyName("stream")]
        public bool? Stream { get; set; } = false;

        [JsonPropertyName("stop")]
        public List<string>? Stop { get; set; }

        [JsonPropertyName("presence_penalty")]
        public double? PresencePenalty { get; set; } = 0;

        [JsonPropertyName("frequency_penalty")]
        public double? FrequencyPenalty { get; set; } = 0;

        [JsonPropertyName("logprobs")]
        public bool? Logprobs { get; set; } = false;

        [JsonPropertyName("top_logprobs")]
        public int? TopLogprobs { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }
    }

    public class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
    }
}

