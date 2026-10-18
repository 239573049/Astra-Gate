using Astra.Core.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Gateway.Routing;

/// <summary>
/// Where one judgement is sent and how: the service address, key, model and deadline. A judgement that has to open a
/// connection first gets <see cref="ConnectAllowance"/> on top of <paramref name="Timeout"/>.
/// </summary>
public sealed record JudgeEndpoint(string BaseUrl, string? ApiKey, string Model, TimeSpan Timeout)
{
    /// <summary>
    /// The extra time a judgement gets when it has to open its connection first: the handshake belongs to the network
    /// (through a proxy it took several seconds), not to the judge, and the connection serves later judgements as well.
    /// </summary>
    public static readonly TimeSpan ConnectAllowance = TimeSpan.FromSeconds(3);

    /// <summary>Called when the judgement has to open a new connection to the service (once, before it is sent).</summary>
    public Action? OnNewConnection { get; init; }
}

/// <summary>Why a judgement failed. Only the kind is ever recorded: the message may quote what the service said.</summary>
public enum JudgeFailure
{
    /// <summary>No answer within the deadline.</summary>
    Timeout,

    /// <summary>The service could not be reached.</summary>
    Network,

    /// <summary>The key was refused (401 / 403).</summary>
    Auth,

    /// <summary>429.</summary>
    RateLimited,

    /// <summary>The service is down or overloaded (5xx, including TypeSafe's 529).</summary>
    Overloaded,

    /// <summary>The service rejected the request (400 / 413 / 415 / 422 …).</summary>
    BadRequest,

    /// <summary>The answer was not what the dialect promises: not JSON, no answer to the question, an unknown choice.</summary>
    BadResponse,
}

/// <summary>A judgement that failed. Smart routing never lets it fail the user's request: it falls back to a grade.</summary>
public sealed class RoutingJudgeException(JudgeFailure kind, string message, int? status = null) : Exception(message)
{
    public JudgeFailure Kind { get; } = kind;

    /// <summary>The HTTP status the service answered with, when it answered.</summary>
    public int? Status { get; } = status;
}

/// <summary>
/// Asks a "decision model" — a classifier that answers with probabilities, not text — which grade fits a request
/// (plan §5.3). One implementation per wire dialect (<see cref="JudgeProtocols"/>): the question is always the same
/// multiple-choice one, only its encoding and the shape of the answer differ.
/// </summary>
public interface IRoutingJudge
{
    /// <summary>One of <see cref="JudgeProtocols.All"/>.</summary>
    string Protocol { get; }

    /// <exception cref="RoutingJudgeException">The service could not give a usable answer.</exception>
    Task<JudgeVerdict> JudgeAsync(JudgeEndpoint endpoint, JudgeQuestion question, CancellationToken ct);
}

/// <summary>The registered routing judges (one per <see cref="JudgeProtocols"/> dialect).</summary>
public sealed class RoutingJudgeRegistry
{
    private readonly Dictionary<string, IRoutingJudge> _judges;

    public RoutingJudgeRegistry(IEnumerable<IRoutingJudge> judges) => _judges = judges.ToDictionary(j => j.Protocol, StringComparer.Ordinal);

    public IRoutingJudge? Get(string protocol) => _judges.GetValueOrDefault(protocol);

    public IRoutingJudge Require(string protocol) =>
        Get(protocol) ?? throw new InvalidOperationException($"No routing judge registered for {protocol}");
}

/// <summary>Registers the routing judges; a new dialect is a class here, a <see cref="JudgeProtocols"/> constant and its tests.</summary>
public static class RoutingJudges
{
    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton<IRoutingJudge, SystemOneJudge>();
        services.AddSingleton<IRoutingJudge, DecisionsJudge>();
        services.AddSingleton<RoutingJudgeRegistry>();
        return services;
    }
}
