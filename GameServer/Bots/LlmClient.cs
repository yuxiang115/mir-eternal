using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Bots
{
    /// <summary>OpenAI 兼容协议的一条消息。</summary>
    public class LlmMessage
    {
        public string Role;
        public string Content;

        public LlmMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    /// <summary>LLM 返回的一次工具调用。</summary>
    public class LlmToolCall
    {
        public string Id;
        public string Name;
        public JObject Arguments;
    }

    /// <summary>一次对话补全的结果:纯文本 + 工具调用列表。</summary>
    public class LlmResult
    {
        public string Content;
        public List<LlmToolCall> ToolCalls = new List<LlmToolCall>();
    }

    /// <summary>
    /// OpenAI 兼容 Chat Completions 客户端(支持智谱 GLM / DeepSeek / OpenAI / Ollama 等)。
    /// 仅在后台线程调用,绝不触碰游戏状态。
    /// </summary>
    public static class LlmClient
    {
        private static readonly HttpClient Http = new HttpClient();

        public static bool IsConfigured(BotLlmConfig config)
        {
            return config != null
                && !string.IsNullOrWhiteSpace(config.BaseUrl)
                && !string.IsNullOrWhiteSpace(config.Model)
                && !string.IsNullOrWhiteSpace(config.ApiKey)
                && !config.ApiKey.StartsWith("在此填入");
        }

        /// <summary>非流式对话补全。tools 为 OpenAI tools 格式的 JSON 数组,可为 null。</summary>
        public static async Task<LlmResult> ChatAsync(BotLlmConfig config, List<LlmMessage> messages, JArray tools)
        {
            var body = new JObject
            {
                ["model"] = config.Model,
                ["temperature"] = config.Temperature,
                ["max_tokens"] = config.MaxTokens,
                ["messages"] = new JArray(messages.Select(m => new JObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? "",
                })),
            };
            if (tools != null && tools.Count > 0)
                body["tools"] = tools;

            var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(config.BaseUrl, "/chat/completions"));
            request.Headers.Add("Authorization", "Bearer " + config.ApiKey);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds)))
            using (var response = await Http.SendAsync(request, timeoutCts.Token))
            {
                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"LLM HTTP {(int)response.StatusCode}: {Truncate(json, 300)}");

                return ParseResponse(JObject.Parse(json));
            }
        }

        private static string BuildUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + path;
        }

        private static LlmResult ParseResponse(JObject json)
        {
            var result = new LlmResult();
            var choice = json["choices"]?.FirstOrDefault() as JObject;
            if (choice == null || choice["message"] == null)
                throw new Exception("LLM 响应缺少 choices/message: " + Truncate(json.ToString(Formatting.None), 300));

            var message = choice["message"];
            result.Content = message["content"]?.Type == JTokenType.String ? message["content"].ToString() : "";

            var toolCalls = message["tool_calls"] as JArray;
            if (toolCalls != null)
            {
                foreach (var token in toolCalls)
                {
                    var call = token as JObject;
                    if (call == null) continue;
                    var function = call["function"] as JObject;
                    if (function == null) continue;

                    var arguments = new JObject();
                    var argsText = function["arguments"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(argsText))
                    {
                        try { arguments = JObject.Parse(argsText); }
                        catch { /* 容忍模型输出非法 JSON,按空参数处理 */ }
                    }

                    result.ToolCalls.Add(new LlmToolCall
                    {
                        Id = call["id"]?.ToString() ?? "",
                        Name = function["name"]?.ToString() ?? "",
                        Arguments = arguments,
                    });
                }
            }
            return result;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }
    }
}
