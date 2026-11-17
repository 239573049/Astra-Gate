using Astra.Data;

namespace Astra.Server.Hosting;

/// <summary>
/// Updates a Docker deployment through a container updater beside it: the Watchtower HTTP API
/// (<c>nickfedor/watchtower</c>, the maintained fork) at <see cref="ServerOptions.UpdaterUrl"/>, run with only the
/// <c>update</c> endpoint, no schedule of its own and label-scoped to Astra's container (see compose.yaml). Astra decides
/// when — its own feed check, channel and <see cref="Astra.Core.AppSettings.UpdateAutoApply"/> — and the updater pulls the
/// image tag the container runs (<c>latest</c> = stable, <c>preview</c>) and recreates the container, which restarts this
/// process on the new version. The server never gets the Docker socket itself. A request is fire-and-forget
/// (<c>async=true</c>): when the image is not on the registry yet (it is pushed after the feed announces the version), nothing
/// changes and the next round asks again (<see cref="RetryAfter"/>).
/// </summary>
public sealed class ContainerUpdater(ServerOptions server, AstraDatabase db, IHttpClientFactory factory, ILogger<ContainerUpdater> logger)
{
    public const string HttpClientName = "container-updater";

    /// <summary>How long after a request for the same version the worker asks again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>Whether this deployment has an updater to ask.</summary>
    public bool Configured => server.UpdaterUrl is not null && server.UpdaterToken is not null;

    /// <summary>
    /// Asks the updater to update now and records the request in the update state. Throws nothing for an updater that
    /// refuses or cannot be reached: the reason is <see cref="UpdateCheckState.ApplyError"/>.
    /// </summary>
    public async Task<UpdateCheckState> RequestAsync(CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("No container updater is configured");
        await _mutex.WaitAsync(ct);
        try
        {
            var state = await db.Settings.GetAsync<UpdateCheckState>(UpdateCheckService.StateKey, ct) ?? new UpdateCheckState();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{server.UpdaterUrl}/v1/update?async=true");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.UpdaterToken);
                using var resp = await factory.CreateClient(HttpClientName).SendAsync(req, ct);
                // 202: started; 429: one is already running — either way an update is under way.
                state.ApplyError = resp.IsSuccessStatusCode || (int)resp.StatusCode == 429
                    ? null
                    : (int)resp.StatusCode == 401 ? "updater-unauthorized" : $"updater-http-{(int)resp.StatusCode}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                state.ApplyError = "updater-unreachable";
                logger.LogWarning("容器更新器无法连接：{Message}", e.Message);
            }
            if (state.ApplyError is { } error) logger.LogWarning("容器更新请求失败：{Error}", error);
            else logger.LogInformation("已请求容器更新器更新到 {Version}", state.AvailableVersion ?? "（最新镜像）");
            state.ApplyVersion = state.AvailableVersion;
            state.ApplyRequestedAt = DateTimeOffset.UtcNow;
            await db.Settings.SetAsync(UpdateCheckService.StateKey, state, ct);
            return state;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Whether the worker should ask the updater for <paramref name="state"/>'s available version now: there is one, and it
    /// was not already asked for within <see cref="RetryAfter"/>.
    /// </summary>
    public static bool Due(UpdateCheckState state, DateTimeOffset now) =>
        state.AvailableVersion is { } available
        && (state.ApplyVersion != available || state.ApplyRequestedAt is not { } at || now - at >= RetryAfter);
}
