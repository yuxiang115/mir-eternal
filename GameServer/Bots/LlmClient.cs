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
    /// <summary>OpenAI 兼容协议的一条消息。assistant 可携带 tool_calls;role=tool 用 ToolCallId 配对回执。</summary>
    public class LlmMessage
    {
        public string Role;
        public string Content;
        /// <summary>assistant 消息携带的原始 tool_calls 数组(官方协议要求原样回传)。</summary>
        public JArray ToolCalls;
        /// <summary>role=tool 时对应的 tool_call_id。</summary>
        public string ToolCallId;

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
        /// <summary>思维链(DeepSeek reasoning_content 等)。只写日志,绝不外发。</summary>
        public string Reasoning;
        public List<LlmToolCall> ToolCalls = new List<LlmToolCall>();
        /// <summary>前缀缓存命中/未命中的输入 token(DeepSeek usage.prompt_cache_hit_tokens);非 0 即支持缓存。</summary>
        public int CacheHitTokens;
        public int CacheMissTokens;
        /// <summary>本次请求真实输入 token 总量(usage.prompt_tokens)——上下文计量的权威值。</summary>
        public int PromptTokens;
        /// <summary>本次输出 token(usage.completion_tokens,含思维链)。</summary>
        public int CompletionTokens;
        /// <summary>模型原始 tool_calls 数组(官方协议要求下一轮原样回传)。</summary>
        public JArray RawToolCalls;
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

        /// <summary>非流式对话补全。tools 为 OpenAI tools 格式的 JSON 数组,可为 null。reasoningEffort 非空时覆盖 ExtraBody 档位(思考分级:计划轮高、执行轮低)。</summary>
        public static async Task<LlmResult> ChatAsync(BotLlmConfig config, List<LlmMessage> messages, JArray tools, string reasoningEffort = null)
        {
            var body = new JObject
            {
                ["model"] = config.Model,
                ["temperature"] = config.Temperature,
                ["max_tokens"] = config.MaxTokens,
                ["messages"] = new JArray(messages.Select(m =>
                {
                    var o = new JObject
                    {
                        ["role"] = m.Role,
                        ["content"] = m.Content ?? "",
                    };
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                        o["tool_calls"] = m.ToolCalls; // assistant 原样回传(官方协议)
                    if (!string.IsNullOrEmpty(m.ToolCallId))
                        o["tool_call_id"] = m.ToolCallId; // tool 角色配对回执
                    return o;
                })),
            };
            if (tools != null && tools.Count > 0)
                body["tools"] = tools;
            if (config.ExtraBody != null)
                body.Merge(config.ExtraBody); // 厂商扩展参数(thinking/reasoning_effort 等)
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
                body["reasoning_effort"] = reasoningEffort; // 思考分级覆盖,优先于 ExtraBody

            var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(config.BaseUrl, "/chat/completions"));
            request.Headers.Add("Authorization", "Bearer " + config.ApiKey);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds)))
            using (var response = await Http.SendAsync(request, timeoutCts.Token))
            {
                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"LLM HTTP {(int)response.StatusCode}: {Truncate(json, 300)}");

                var parsed = JObject.Parse(json);
                var result = ParseResponse(parsed);
                var usage = parsed["usage"] as JObject;
                if (usage != null)
                {
                    result.CacheHitTokens = usage["prompt_cache_hit_tokens"]?.Type == JTokenType.Integer ? usage["prompt_cache_hit_tokens"].Value<int>() : 0;
                    result.CacheMissTokens = usage["prompt_cache_miss_tokens"]?.Type == JTokenType.Integer ? usage["prompt_cache_miss_tokens"].Value<int>() : 0;
                    // 上下文计量的权威值:API 报的真实输入总量(缺失时回退 hit+miss)
                    result.PromptTokens = usage["prompt_tokens"]?.Type == JTokenType.Integer ? usage["prompt_tokens"].Value<int>() : result.CacheHitTokens + result.CacheMissTokens;
                    result.CompletionTokens = usage["completion_tokens"]?.Type == JTokenType.Integer ? usage["completion_tokens"].Value<int>() : 0;
                }
                return result;
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
            result.Reasoning = message["reasoning_content"]?.Type == JTokenType.String ? message["reasoning_content"].ToString() : "";

            var toolCalls = message["tool_calls"] as JArray;
            if (toolCalls != null && toolCalls.Count > 0)
                result.RawToolCalls = (JArray)toolCalls.DeepClone();
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
