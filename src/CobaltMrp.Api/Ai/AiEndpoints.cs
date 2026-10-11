using CobaltMrp.Api.Api;
using CobaltMrp.Api.Settings;

namespace CobaltMrp.Api.Ai;

public static class AiEndpoints
{
    public static void MapAi(this RouteGroupBuilder api)
    {
        // Any signed-in user (Admin or Planner). The key is never part of any answer here.
        var g = api.MapGroup("/ai");

        g.MapGet("/status", (AiAssistant ai) =>
        {
            var s = ai.Status();
            return Results.Ok(new { enabled = s.Enabled, configured = s.Configured, model = s.Model });
        });

        g.MapPost("/ask", async (AiAskRequest? req, HttpContext ctx, AiAssistant ai, AiRateLimiter limiter, CancellationToken ct) =>
        {
            if (req is null || req.RunId == Guid.Empty) return Results.BadRequest(new { message = "runId is required." });
            if (string.IsNullOrWhiteSpace(req.Question)) return Results.BadRequest(new { message = "Type a question first." });
            if (req.Question.Length > AiAssistant.MaxQuestionChars)
                return Results.BadRequest(new { message = $"The question is too long ({AiAssistant.MaxQuestionChars} characters at most)." });
            return await Guard(ctx, limiter, ai, async () =>
            {
                var a = await ai.AskAsync(req, User(ctx), ct);
                return Results.Ok(new { answer = a.Text, toolCalls = a.ToolCalls, usage = a.Usage });
            });
        });

        g.MapPost("/summary", async (AiSummaryRequest? req, HttpContext ctx, AiAssistant ai, AiRateLimiter limiter, CancellationToken ct) =>
        {
            if (req is null || req.RunId == Guid.Empty) return Results.BadRequest(new { message = "runId is required." });
            return await Guard(ctx, limiter, ai, async () =>
            {
                var a = await ai.SummariseAsync(req.RunId, User(ctx), ct);
                return Results.Ok(new { summary = a.Text, toolCalls = a.ToolCalls, usage = a.Usage });
            });
        });
    }

    private static string User(HttpContext ctx) => ctx.User.Identity?.Name ?? "unknown";

    /// <summary>Common error mapping: 400 off/unconfigured, 404 run, 429 rate limit, 502 provider failure.</summary>
    private static async Task<IResult> Guard(HttpContext ctx, AiRateLimiter limiter, AiAssistant ai, Func<Task<IResult>> call)
    {
        // Check the switch first so a disabled assistant does not use up the caller's allowance.
        var s = ai.Status();
        if (!s.Enabled) return Results.BadRequest(new { message = "The AI assistant is turned off. An administrator can enable it in Settings > AI." });
        if (!s.Configured) return Results.BadRequest(new { message = "The AI assistant has no API key yet. An administrator can add one in Settings > AI." });
        if (!limiter.TryAcquire(User(ctx)))
            return Results.Json(new { message = "Too many AI requests. Wait a minute and try again." }, statusCode: 429);
        try { return await call(); }
        catch (AiConfigException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (AiRunNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
        // 502, not the provider's own status: a 401 here would make the UI think the user's session expired.
        catch (AiException e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }

    /// <summary>Settings > AI test buttons: always 200 with ok true/false, never 500, never the key.</summary>
    public static void MapAiSettings(this RouteGroupBuilder settingsGroup)
    {
        settingsGroup.MapPost("/test/ai", async (AiSettings? body, SettingsService settings, OpenRouterClient client, CancellationToken ct) =>
        {
            var (key, model, _) = Resolve(body, settings);
            if (string.IsNullOrWhiteSpace(key)) return Results.Ok(new TestResult(false, "Enter the OpenRouter API key first."));
            try
            {
                var msgs = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Reply with the single word OK." },
                };
                var r = await client.ChatAsync(key, model, msgs, null, 8, ct);
                return Results.Ok(new TestResult(true, $"Connected. {model} answered ({r.Usage.TotalTokens} tokens used)."));
            }
            catch (AiException e) { return Results.Ok(new TestResult(false, e.Message)); }
        });

        settingsGroup.MapPost("/ai/models", async (AiSettings? body, SettingsService settings, OpenRouterClient client, CancellationToken ct) =>
        {
            var (key, _, _) = Resolve(body, settings);
            try
            {
                var models = await client.ListModelsAsync(key, ct);
                return Results.Ok(new { ok = true, message = $"{models.Count} models available.", models });
            }
            catch (AiException e) { return Results.Ok(new { ok = false, message = e.Message, models = Array.Empty<string>() }); }
        });
    }

    /// <summary>The unsaved values from the form win; a blank key means the stored one.</summary>
    private static (string Key, string Model, int MaxTokens) Resolve(AiSettings? body, SettingsService settings)
    {
        var stored = settings.Get().Ai;
        var key = string.IsNullOrWhiteSpace(body?.ApiKey) ? stored.ApiKey : body!.ApiKey.Trim();
        var model = string.IsNullOrWhiteSpace(body?.Model) ? stored.Model : body!.Model.Trim();
        return (key, model, stored.MaxTokens);
    }
}

