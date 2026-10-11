using System.Collections.Concurrent;

namespace Astra.Plugins.Packages;

/// <summary>
/// Long plugin operations (installs, updates, Bun's download) run as jobs the plugins page follows:
/// each keeps its log, so a slow <c>bun add</c> shows what it is doing. Finished jobs are kept for a
/// while for the page to read their outcome.
/// </summary>
public sealed class PluginJobs
{
    private const int MaxLogLines = 400;
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    /// <summary>Starts <paramref name="work"/> in the background and returns its job at once.</summary>
    public PluginJob Start(string action, string target, Func<Action<string>, CancellationToken, Task<string?>> work)
    {
        Prune();
        var job = new Job(Guid.NewGuid().ToString("N")[..12], action, target);
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(job.Append, CancellationToken.None);
                job.Finish(null, result);
            }
            catch (Exception e)
            {
                job.Append(e.Message);
                job.Finish(e.Message, null);
            }
        });
        return job.Snapshot();
    }

    public PluginJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Keep;
        foreach (var (id, job) in _jobs)
            if (job.FinishedAt is { } at && at < cutoff) _jobs.TryRemove(id, out _);
    }

    private sealed class Job(string id, string action, string target)
    {
        private readonly object _gate = new();
        private readonly List<string> _log = [];
        private string _state = "running";
        private string? _error;
        private string? _result;

        public string Id => id;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; private set; }

        public void Append(string line)
        {
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > MaxLogLines) _log.RemoveAt(0);
            }
        }

        public void Finish(string? error, string? result)
        {
            lock (_gate)
            {
                _state = error is null ? "succeeded" : "failed";
                _error = error;
                _result = result;
                FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        public PluginJob Snapshot()
        {
            lock (_gate)
                return new PluginJob(id, action, target, _state, StartedAt, FinishedAt, [.. _log], _error, _result);
        }
    }
}

/// <summary>A plugin job as the page reads it.</summary>
/// <param name="Action">add, update, remove or runtime.</param>
/// <param name="Target">The spec (or name) it acts on; "bun" for the runtime.</param>
/// <param name="State">running, succeeded or failed.</param>
/// <param name="Result">What it produced: the spec an add saved, for instance.</param>
public sealed record PluginJob(
    string Id, string Action, string Target, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    List<string> Log, string? Error, string? Result);
