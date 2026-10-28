using System.Windows.Input;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Kit;

/// <summary>Web <c>EmptyState</c>: Arc's empty state with the web's generous padding (<c>px-6 py-12</c>).</summary>
public sealed class EmptyStateView : UserControl
{
    private readonly EmptyState _inner = new();

    /// <summary>The headline.</summary>
    public string? Title { get => _inner.Title; set => _inner.Title = value; }

    /// <summary>One or two sentences on what to do (web <c>detail</c>).</summary>
    public string? Detail { get => _inner.Description; set => _inner.Description = value; }

    /// <summary>A control, or a 24x24 Lucide geometry.</summary>
    public object? Icon { get => _inner.Icon; set => _inner.Icon = value; }

    /// <summary>The call to action (usually a button).</summary>
    public object? Action { get => _inner.Action; set => _inner.Action = value; }

    /// <summary>An empty state; set the properties.</summary>
    public EmptyStateView()
    {
        Padding = new Thickness(24, 48);
        Content = _inner;
    }

    /// <summary>An empty state with title and detail.</summary>
    public EmptyStateView(string title, string? detail = null, object? icon = null, object? action = null) : this()
    {
        Title = title;
        Detail = detail;
        if (icon is not null) Icon = icon;
        Action = action;
    }
}

/// <summary>
/// A failed load: an Arc empty state with an alert icon in the danger color, the message, and a Retry button
/// (<c>common.retry</c>) when <see cref="RetryCommand"/> is set. Pages show it in place of their content, like the web's
/// error branches.
/// </summary>
public sealed class ErrorState : UserControl
{
    /// <summary>What went wrong (the error's message).</summary>
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<ErrorState, string?>(nameof(Message));

    /// <summary>Headline; null shows <c>common.error</c>.</summary>
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ErrorState, string?>(nameof(Title));

    /// <summary>Runs on Retry; null hides the button.</summary>
    public static readonly StyledProperty<ICommand?> RetryCommandProperty = AvaloniaProperty.Register<ErrorState, ICommand?>(nameof(RetryCommand));

    private readonly EmptyState _inner = new();
    private readonly ArcButton _retry = new() { Variant = ButtonVariant.Secondary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Center };

    /// <inheritdoc cref="MessageProperty"/>
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="RetryCommandProperty"/>
    public ICommand? RetryCommand { get => GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    /// <summary>An error state; set the properties.</summary>
    public ErrorState()
    {
        Padding = new Thickness(24, 48);
        var icon = new LucideIcon { Kind = LucideIconKind.CircleAlert, Size = 24 };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Danger);
        _inner.Icon = icon;
        _retry.IsVisible = false;
        _inner.Action = _retry;
        Content = _inner;
        Apply();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
        Apply();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageProperty || change.Property == TitleProperty || change.Property == RetryCommandProperty) Apply();
    }

    private void Apply()
    {
        _inner.Title = Title ?? Loc.T("common.error");
        _inner.Description = Message;
        _retry.Content = Loc.T("common.retry");
        _retry.Command = RetryCommand;
        _retry.IsVisible = RetryCommand is not null;
    }
}

/// <summary>Web <c>Spinner</c>: Arc's skeleton while a page's data loads.</summary>
public sealed class LoadingState : UserControl
{
    /// <summary>Text lines of the placeholder (1-6).</summary>
    public int Lines { get => _inner.Lines; set => _inner.Lines = value; }

    /// <summary>Accessible name of the placeholder (Arc's default is English; pass a localized one).</summary>
    public string Label { get => _inner.Label; set => _inner.Label = value; }

    private readonly Skeleton _inner = new() { Lines = 2 };

    /// <summary>A two-line skeleton.</summary>
    public LoadingState() => Content = _inner;

    /// <summary>A skeleton with <paramref name="lines"/> lines.</summary>
    public LoadingState(int lines) : this() => Lines = lines;
}
