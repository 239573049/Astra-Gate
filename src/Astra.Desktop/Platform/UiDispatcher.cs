using Astra.Desktop.Core.Api;
using Avalonia.Threading;

namespace Astra.Desktop.Platform;

/// <summary>The query layer's UI marshalling over Avalonia's dispatcher (<c>Dispatcher.UIThread.Post</c>).</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>The shared instance.</summary>
    public static readonly AvaloniaUiDispatcher Instance = new();

    /// <inheritdoc />
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
