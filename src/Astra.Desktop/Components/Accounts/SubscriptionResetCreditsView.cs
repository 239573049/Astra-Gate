using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// A codex account's rate-limit reset cards (web SubscriptionResetCredits.tsx): the upstream list (available / redeemed / expired, each with
/// its expiry), fetched live while shown because a card is used up or expires; "use" asks first and the server refreshes the quota after.
/// </summary>
internal sealed class SubscriptionResetCreditsView : UserControl
{
    private readonly AccountsApi _api;
    private readonly IFeedback _feedback;
    private readonly ProviderAccountDto _account;
    private readonly string _providerId;
    private readonly Query<JsonObject> _query;
    private IDisposable? _token;
    private string? _pending;

    /// <summary>Creates the group for <paramref name="account"/> of provider <paramref name="providerId"/>.</summary>
    public SubscriptionResetCreditsView(AccountsApi api, IFeedback feedback, ProviderAccountDto account, string providerId)
    {
        _api = api;
        _feedback = feedback;
        _account = account;
        _providerId = providerId;
        _query = api.ResetCredits(account.Id);
        Build();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _query.PropertyChanged += OnChanged;
        // web: enabled only while the login is active.
        if (_account.Status == "active") _token = _query.Activate();
        Build();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _query.PropertyChanged -= OnChanged;
        _token?.Dispose();
        _token = null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var group = new SettingsGroup { Title = Loc.T("providers.subscription.credits.title"), Footer = Loc.T("providers.subscription.credits.footer") };
        var list = _query.HasData ? ResetCreditList.Parse(_query.Data) : null;
        if (_query.IsLoading) group.Rows.Add(new Border { Padding = new Thickness(16), Child = new LoadingState(2) });
        if (_query.IsError && !_query.HasData)
            group.Rows.Add(new Border { Padding = new Thickness(16, 12), Child = AccountsUi.Text(Loc.T("providers.subscription.credits.unavailable"), 12, ArcDesignTokenKind.TextSecondary, wrap: true) });
        if (list is { Credits.Count: 0 })
        {
            var none = AccountsUi.Text(Loc.T("providers.subscription.credits.none"), 12, ArcDesignTokenKind.TextSecondary, wrap: true);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.TextAlignment = Avalonia.Media.TextAlignment.Center;
            group.Rows.Add(new Border { Padding = new Thickness(16, 20), Child = none });
        }
        foreach (var card in list?.Credits ?? []) group.Rows.Add(Row(card));
        if (list is { Credits.Count: > 0 })
            group.Rows.Add(new Border { Padding = new Thickness(16, 8), Child = AccountsUi.Note(Loc.T("providers.subscription.credits.availableCount", ("count", list.AvailableCount))) });
        Content = group;
    }

    private Control Row(ResetCredit card)
    {
        var details = new List<string>();
        if (!string.IsNullOrEmpty(card.Description)) details.Add(card.Description);
        if (card.ExpiresAt is { } exp) details.Add(Loc.T("providers.subscription.credits.expires", ("when", SettingsFormat.Full(exp, Loc.Language))));
        if (card.RedeemedAt is { } red) details.Add(Loc.T("providers.subscription.credits.redeemedAt", ("when", SettingsFormat.Full(red, Loc.Language))));
        if (card.SupportedByPlan == false) details.Add(Loc.T("providers.subscription.credits.notInPlan"));

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(AccountsUi.Icon(LucideIconKind.Gift, 14, ArcDesignTokenKind.TextSecondary));
        label.Children.Add(KitStyle.Text(card.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
        var row = new SettingsRow { Label = label, Detail = details.Count == 0 ? null : string.Join(" · ", details), Opacity = card.Spent ? 0.55 : 1 };
        var tone = card.Status switch { "available" => "green", "expired" => "orange", _ => "neutral" };
        row.Trailing.Add(AccountsUi.Badge(Loc.T("providers.subscription.credits.status." + card.Status), tone));
        row.Trailing.Add(AccountsUi.Button(Loc.T("providers.subscription.credits.consume"), () => _ = SpendAsync(card), ButtonVariant.Ghost, LucideIconKind.Ticket,
            enabled: !card.Spent, loading: _pending == card.Id));
        return row;
    }

    private async Task SpendAsync(ResetCredit card)
    {
        var ok = await _feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("providers.subscription.credits.consumeConfirm", ("title", card.Title ?? card.ResetType ?? "")),
            Loc.T("providers.subscription.credits.consumeDetail"), Loc.T("providers.subscription.credits.consume"), null, true));
        if (!ok) return;
        _pending = card.Id;
        Build();
        try
        {
            await _api.ConsumeResetCreditAsync(_providerId, _account.Id, card.Id);
            _feedback.Success(Loc.T("providers.subscription.credits.consumed"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _feedback.Error(e is ApiException { Status: 409 } ? AccountRules.ErrorText(e) : $"{Loc.T("providers.subscription.credits.consumeFailed")}：{AccountRules.ErrorText(e)}");
        }
        finally
        {
            _pending = null;
            Build();
        }
    }
}
