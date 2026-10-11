using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CobaltMrp.Api.Ai;

/// <summary>A failed call to the AI provider, with a message that is safe and readable for the user.</summary>
public sealed class AiException(int upstreamStatus, string message) : Exception(message)
{
    /// <summary>The HTTP status OpenRouter answered with, or 0 when there was none (network, timeout, unreadable reply).</summary>
    public int UpstreamStatus { get; } = upstreamStatus;
}

public sealed record ChatUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
    public static ChatUsage operator +(ChatUsage a, ChatUsage b) => new(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens);
}

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>One model answer: either final text, or tool calls the caller must run and feed back.</summary>
public sealed record ChatResult(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls, ChatUsage Usage, JsonObject AssistantMessage);

/// <summary>
/// Minimal OpenRouter client (OpenAI-compatible chat completions with tool calling) over plain HttpClient, no SDK.
/// The API key is passed per call and is never stored, logged, or included in an error message.
/// </summary>
public sealed class OpenRouterClient
{
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>How long one provider call may take before it is abandoned.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public OpenRouterClient(HttpClient http, string? baseUrl = null, TimeSpan? timeout = null)
    {
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/');
        if (timeout is { } t) Timeout = t;
    }

    public async Task<ChatResult> ChatAsync(string apiKey, string model, JsonArray messages, JsonArray? tools,
        int maxTokens, CancellationToken ct)
    {
        // Several ids (comma separated): the first is the primary and the rest are tried by OpenRouter in order.
        var ids = model.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0).ToList();
        var body = new JsonObject
        {
            ["model"] = ids.Count > 0 ? ids[0] : model,
            ["messages"] = JsonNode.Parse(messages.ToJsonString()),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
        };
        if (ids.Count > 1) body["models"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (tools is { Count: > 0 }) body["tools"] = JsonNode.Parse(tools.ToJsonString());

        var json = await SendAsync(HttpMethod.Post, "/chat/completions", apiKey, body.ToJsonString(), ct);
        return Parse(json);
    }

    /// <summary>The model ids OpenRouter offers (GET /models), sorted.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string? apiKey, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/models", apiKey, null, ct);
        var ids = (json?["data"] as JsonArray)?
            .Select(n => n?["id"]?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(1000).ToList();
        if (ids is null) throw new AiException(0, "OpenRouter returned a model list that could not be read.");
        return ids;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, string? apiKey, string? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(method, _baseUrl + path);
        if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("X-Title", "Cobalt MRP");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        string text; int status;
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            status = (int)res.StatusCode;
            text = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException(0, $"OpenRouter did not answer within {(int)Timeout.TotalSeconds} seconds. Try again, or pick a faster model.");
        }
        catch (HttpRequestException)
        {
            throw new AiException(0, "Could not reach OpenRouter. Check the server's internet access and proxy settings.");
        }

        JsonNode? json = null;
        try { if (!string.IsNullOrWhiteSpace(text)) json = JsonNode.Parse(text); } catch (JsonException) { }

        if (status is < 200 or >= 300) throw new AiException(status, MapError(status, ErrorText(json), apiKey));
        if (json is not JsonObject) throw new AiException(0, "OpenRouter returned a reply that could not be read (not valid JSON).");
        // OpenRouter sometimes answers 200 with an error object (for example when the model provider failed).
        if (json["error"] is JsonObject err && json["choices"] is null)
        {
            var code = err["code"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : 502;
            throw new AiException(code, MapError(code, ErrorText(json), apiKey));
        }
        return json;
    }

    private static string? ErrorText(JsonNode? json)
    {
        var e = json?["error"];
        if (e is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return e?["message"] is JsonValue m && m.TryGetValue<string>(out var t) ? t : null;
    }

    /// <summary>Friendly text for the statuses a planner will actually meet. Never contains the key.</summary>
    public static string MapError(int status, string? detail, string? apiKey = null)
    {
        var d = Scrub(detail, apiKey);
        return status switch
        {
            401 => "OpenRouter rejected the API key (401). Check the key in Settings > AI.",
            402 => "OpenRouter says the account has insufficient credits (402). Add credits at openrouter.ai or choose a free model.",
            403 => "OpenRouter refused the request (403)" + (d is null ? "." : ": " + d),
            404 => "OpenRouter could not find that model or endpoint (404). Check the model name in Settings > AI." + (d is null ? "" : " " + d),
            408 => "OpenRouter timed out (408). Try again.",
            429 => "OpenRouter is rate limiting requests (429). Wait a moment and try again.",
            >= 500 => $"OpenRouter or the model provider had a problem ({status}). Try again shortly." + (d is null ? "" : " " + d),
            _ => $"OpenRouter rejected the request ({status})" + (d is null ? "." : ": " + d),
        };
    }

    private static string? Scrub(string? text, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (!string.IsNullOrEmpty(apiKey)) t = t.Replace(apiKey, "***");
        t = t.ReplaceLineEndings(" ");
        return t.Length > 240 ? t[..240] + "..." : t;
    }

    private static ChatResult Parse(JsonNode? json)
    {
        var msg = ((json?["choices"] as JsonArray)?.FirstOrDefault() as JsonObject)?["message"] as JsonObject;
        if (msg is null) throw new AiException(0, "OpenRouter returned a reply without an answer. Try again or pick another model.");

        string? content = null;
        switch (msg["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s): content = s; break;
            case JsonArray parts:
                content = string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? ""));
                break;
        }

        var calls = new List<ToolCallRequest>();
        if (msg["tool_calls"] is JsonArray tcs)
            foreach (var tc in tcs)
            {
                var name = tc?["function"]?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var id = tc?["id"]?.GetValue<string>() ?? $"call_{calls.Count}";
                var args = tc?["function"]?["arguments"] switch
                {
                    JsonValue a when a.TryGetValue<string>(out var s) => s,
                    JsonNode other => other.ToJsonString(),
                    _ => "{}",
                };
                calls.Add(new ToolCallRequest(id, name!, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            }

        int Num(string key) => json?["usage"]?[key] is JsonValue u && u.TryGetValue<int>(out var n) ? n : 0;
        // The assistant message is replayed to the model in the next round: keep only what the API expects.
        var replay = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (calls.Count > 0)
            replay["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        return new ChatResult(content, calls, new ChatUsage(Num("prompt_tokens"), Num("completion_tokens")), replay);
    }
}
