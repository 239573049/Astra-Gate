using Astra.Api.Contracts;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// One table row (web <c>Row</c> of RequestsPage): the freshest copy of a request. The table keeps the same instance across live updates and
/// refetches (its row key is <see cref="Id"/>), so a cell redraws from <see cref="Summary"/> when that changes and the rest of the table stays put.
/// The sortable values are plain properties the columns read (web: the <c>Row</c> fields next to <c>r</c>).
/// </summary>
public sealed class RequestRowViewModel : ObservableObject
{
    private RequestSummaryDto _summary;

    /// <summary>A row for <paramref name="summary"/>.</summary>
    public RequestRowViewModel(RequestSummaryDto summary) => _summary = summary;

    /// <summary>The request's id (the table's row key).</summary>
    public string Id => _summary.Id;

    /// <summary>The freshest copy: the live feed's while the request is in flight (or just finished and awaiting the list refetch), else the stored row.</summary>
    public RequestSummaryDto Summary => _summary;

    /// <summary>Takes a fresher copy; a no-op for the same instance.</summary>
    public void Update(RequestSummaryDto summary)
    {
        if (ReferenceEquals(summary, _summary)) return;
        _summary = summary;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Sort value of the time column.</summary>
    public DateTimeOffset Time => _summary.StartedAtUtc;

    /// <summary>Sort value of the client column (the shown name).</summary>
    public string ClientName => RequestPresentation.RequestClientName(_summary.ClientKind, _summary.TokenId, TraceTexts.FromLoc);

    /// <summary>Sort value of the token column.</summary>
    public string Token => _summary.TokenName ?? "";

    /// <summary>Sort value of the model column.</summary>
    public string Model => _summary.RequestedModel ?? "";

    /// <summary>Sort value of the provider column.</summary>
    public string Provider => _summary.ProviderName ?? "";

    /// <summary>Sort value of the status column.</summary>
    public string Status => _summary.Status;

    /// <summary>Sort value of the TTFT column (null sorts last).</summary>
    public double? Ttft => _summary.TtftMs;

    /// <summary>Sort value of the speed column (null sorts last).</summary>
    public double? Tps => _summary.OutputTps;

    /// <summary>Sort value of the tokens column: input plus output.</summary>
    public long Tokens => _summary.TotalInputTokens + _summary.TotalOutputTokens;

    /// <summary>Sort value of the cache column: tokens read from the cache.</summary>
    public long Cache => _summary.CacheReadTokens;

    /// <summary>Sort value of the cost column.</summary>
    public long Cost => _summary.CostNanoUsd;
}
