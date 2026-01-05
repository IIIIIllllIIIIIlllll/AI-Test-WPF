using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AI_Test.OpenAI.Models
{
    public class ChatCompletionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = "chat.completion";

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<ChatCompletionChoice> Choices { get; set; } = new List<ChatCompletionChoice>();

        [JsonPropertyName("usage")]
        public ChatCompletionUsage? Usage { get; set; }

        [JsonPropertyName("system_fingerprint")]
        public string? SystemFingerprint { get; set; }
    }

    public class ChatCompletionChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public ChatMessage Message { get; set; } = new ChatMessage();

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }

        [JsonPropertyName("logprobs")]
        public ChatCompletionLogProbs? LogProbs { get; set; }
    }

    public class ChatCompletionLogProbs
    {
        [JsonPropertyName("content")]
        public List<ChatCompletionTokenLogprob>? Content { get; set; }
    }

    public class ChatCompletionTokenLogprob
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("logprob")]
        public double Logprob { get; set; }

        [JsonPropertyName("bytes")]
        public List<int>? Bytes { get; set; }

        [JsonPropertyName("top_logprobs")]
        public List<ChatCompletionTopLogprob>? TopLogprobs { get; set; }
    }

    public class ChatCompletionTopLogprob
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("logprob")]
        public double Logprob { get; set; }

        [JsonPropertyName("bytes")]
        public List<int>? Bytes { get; set; }
    }

    public class ChatCompletionUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}

