namespace Astra.Core.Requests;

/// <summary>
/// What a request's context window looked like (<c>requests.context_json</c>, Json.Storage). The window and the max output are the
/// effective model's at request time; <see cref="Shares"/> is an estimate of what the prompt was made of (characters of the
/// request body, not a tokenizer), scaled to the reported input tokens on the read side. Idea from magpie's per-request
/// context-window usage (MIT).
/// </summary>
/// <param name="Window">The model's context window in tokens; null when its metadata has none.</param>
/// <param name="MaxOutput">The model's max output tokens; null when unknown.</param>
/// <param name="Shares">How the prompt splits; null when the body could not be read (the log then shows only what is known).</param>
public sealed record ContextReport(long? Window, long? MaxOutput, ContextShares? Shares);

/// <summary>Shares of the prompt (each 0..1, summing to 1): system instructions, tool definitions, earlier messages, the latest turn.</summary>
public sealed record ContextShares(double System, double Tools, double History, double Latest);
