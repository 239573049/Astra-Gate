using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tokens;

/// <summary>
/// The tokens page's commands (web <c>useCreateToken</c> / <c>useUpdateToken</c> / <c>useResetToken</c> / <c>useDeleteToken</c> /
/// <c>useRevealToken</c> and the handlers in <c>TokenRow</c>): each calls the admin API, refreshes the shared token and client lists
/// like the web's <c>useTokenMutation</c>, and reports through <see cref="IFeedback"/>. The clipboard is passed in (it belongs to
/// the window), so the whole flow is testable.
/// </summary>
public sealed class TokenActions
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly IFeedback _feedback;
    private readonly Translate _t;

    /// <summary>Creates the commands over the app's services.</summary>
    public TokenActions(ApiClient api, QueryClient queries, IFeedback feedback, Translate t)
    {
        _api = api;
        _queries = queries;
        _feedback = feedback;
        _t = t;
    }

    /// <summary>Creates a token (name trimmed; <paramref name="providerId"/> empty = no direct calls); the created token, or null after an error toast.</summary>
    public async Task<TokenDto?> CreateAsync(string name, string? providerId, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject { ["name"] = name.Trim(), ["providerId"] = string.IsNullOrEmpty(providerId) ? null : providerId };
            var token = await _api.PostAsync<JsonObject, TokenDto>("/api/tokens", body, ct);
            Refresh();
            if (token is not null) _feedback.Success(_t("tokens.created", ("name", token.Name)));
            return token;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Fail(e);
            return null;
        }
    }

    /// <summary>Patches a token (settings sheet save, enable / disable); true when it went through.</summary>
    public async Task<bool> UpdateAsync(string id, JsonObject patch, string? done = null, CancellationToken ct = default)
    {
        try
        {
            await _api.PatchAsync<JsonObject, TokenDto>("/api/tokens/" + ApiClient.Enc(id), patch, ct);
            Refresh();
            if (done is not null) _feedback.Success(done);
            return true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Fail(e);
            return false;
        }
    }

    /// <summary>
    /// The enable / disable menu entry: disabling asks first (it cuts off every client using the key); enabling does not.
    /// </summary>
    public async Task ToggleAsync(TokenDto token, CancellationToken ct = default)
    {
        if (token.Enabled && !await _feedback.ConfirmAsync(new ConfirmRequest(
                _t("tokens.disableConfirm", ("name", token.Name)), _t("tokens.disableDetail"), _t("tokens.disable"), Destructive: true), ct))
            return;
        await UpdateAsync(token.Id, new JsonObject { ["enabled"] = !token.Enabled }, null, ct);
    }

    /// <summary>Asks, then replaces the key; client configs holding the old one are rewritten (and the ones that could not be are reported).</summary>
    public async Task ResetAsync(TokenDto token, CancellationToken ct = default)
    {
        if (!await _feedback.ConfirmAsync(new ConfirmRequest(
                _t("tokens.resetConfirm", ("name", token.Name)), _t("tokens.resetDetail"), _t("tokens.reset"), Destructive: true), ct))
            return;
        try
        {
            var result = await _api.PostAsync<TokenMutationResultDto>("/api/tokens/" + ApiClient.Enc(token.Id) + "/reset", ct);
            Refresh();
            if (result is not null) ReportRewrite(result, _t("tokens.resetDone", ("count", result.Rewritten.Count)));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Fail(e);
        }
    }

    /// <summary>Asks, then deletes the token (the default one cannot be: the menu does not offer it).</summary>
    public async Task DeleteAsync(TokenDto token, CancellationToken ct = default)
    {
        if (!await _feedback.ConfirmAsync(new ConfirmRequest(
                _t("tokens.deleteConfirm", ("name", token.Name)), _t("tokens.deleteDetail"), _t("common.delete"), Destructive: true), ct))
            return;
        try
        {
            var result = await _api.DeleteAsync<TokenMutationResultDto>("/api/tokens/" + ApiClient.Enc(token.Id), ct);
            Refresh();
            if (result is not null) ReportRewrite(result, _t("tokens.deleted", ("name", token.Name)));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Fetches the plaintext key (only when the user copies it: the list carries masks) and hands it to <paramref name="copy"/>;
    /// toasts "copied" or "copy failed". The key never touches a log or the UI.
    /// </summary>
    public async Task CopyKeyAsync(string id, Func<string, Task<bool>> copy, CancellationToken ct = default)
    {
        try
        {
            var secret = await _api.PostAsync<TokenSecretDto>("/api/tokens/" + ApiClient.Enc(id) + "/reveal", ct);
            if (secret is null) return;
            if (await copy(secret.Token)) _feedback.Success(_t("tokens.copied"));
            else _feedback.Error(_t("common.error"), _t("tokens.copyFailed"));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Fail(e);
        }
    }

    /// <summary>Copies <paramref name="text"/> (an address or the join command) and toasts <paramref name="done"/> or "copy failed".</summary>
    public async Task CopyTextAsync(string text, string done, Func<string, Task<bool>> copy)
    {
        if (await copy(text)) _feedback.Success(done);
        else _feedback.Error(_t("common.error"), _t("tokens.copyFailed"));
    }

    private void ReportRewrite(TokenMutationResultDto r, string done)
    {
        _feedback.Success(done);
        if (r.Skipped.Count > 0)
            _feedback.Error(_t("common.error"), _t("tokens.skipped", ("clients", string.Join(", ", r.Skipped.Select(k => ClientCatalog.Name(k))))));
    }

    /// <summary>A token mutation changes both lists (the clients carry their token's key).</summary>
    private void Refresh()
    {
        _queries.Invalidate(QueryKeys.Tokens);
        _queries.Invalidate(QueryKeys.Clients);
    }

    private void Fail(Exception e) => _feedback.Error(_t("common.error"), e.Message);
}
