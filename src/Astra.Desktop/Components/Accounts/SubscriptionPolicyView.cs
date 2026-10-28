using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The client policy group of a subscription provider (web SubscriptionPolicy.tsx): "Claude Code only" and "other clients present as
/// Claude Code", with the warning that goes with letting any client in. Shows nothing until the policy loads, on a non-subscription
/// provider, and when there is nothing to set (<see cref="SubscriptionPolicyRules.Visible"/>). The switches are controlled: they show the
/// saved policy, a click only asks the server.
/// </summary>
internal sealed class SubscriptionPolicyView : UserControl
{
    private readonly AccountsApi _api;
    private readonly IFeedback _feedback;
    private readonly ProviderDto _provider;
    private readonly Query<SubscriptionPolicyDto> _policy;
    private IDisposable? _token;
    private bool _saving;

    /// <summary>Creates the group for <paramref name="provider"/>.</summary>
    public SubscriptionPolicyView(AccountsApi api, IFeedback feedback, ProviderDto provider)
    {
        _api = api;
        _feedback = feedback;
        _provider = provider;
        _policy = api.Policy(provider.Id);
    }

    private bool IsSubscription => _provider.AuthScheme == "oauth-subscription";

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
        if (!IsSubscription) return;
        _policy.PropertyChanged += OnChanged;
        _token = _policy.Activate();
        Build();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
        _policy.PropertyChanged -= OnChanged;
        _token?.Dispose();
        _token = null;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Data" or "Status") Build();
    }

    private void Build()
    {
        if (!IsSubscription || _policy.Data is not { } p || !SubscriptionPolicyRules.Visible(p))
        {
            Content = null;
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var group = new SettingsGroup { Title = Loc.T("providers.subscription.policy.title"), Footer = Loc.T("providers.subscription.policy.footer") };
        if (SubscriptionPolicyRules.ShowClientPolicy(p))
            group.Rows.Add(Row("providers.subscription.policy.claudeCodeOnly", "providers.subscription.policy.claudeCodeOnlyDetail", p.ClientPolicy == "claude-code-only",
                on => new SubscriptionPolicyPatch(on ? "claude-code-only" : "any", null, null)));
        if (p.ClaudeSubscription)
            group.Rows.Add(Row("providers.subscription.policy.mimic", "providers.subscription.policy.mimicDetail", p.MimicClaudeCode,
                on => new SubscriptionPolicyPatch(null, null, on)));
        if (SubscriptionPolicyRules.WarningKey(p) is { } warning)
            group.Rows.Add(new Border { Padding = new Thickness(16, 12), Child = new Alert { Tone = AlertTone.Warning, Title = Loc.T(warning) } });
        Content = group;
    }

    private SettingsRow Row(string label, string detail, bool on, Func<bool, SubscriptionPolicyPatch> patch)
    {
        var row = new SettingsRow { Label = Loc.T(label), Detail = Loc.T(detail) };
        var toggle = new ControlledSwitch { IsChecked = on, IsEnabled = !_saving };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T(label));
        toggle.ToggleRequested += want => _ = SaveAsync(patch(want));
        row.Trailing.Add(toggle);
        return row;
    }

    private async Task SaveAsync(SubscriptionPolicyPatch patch)
    {
        _saving = true;
        Build();
        try
        {
            await _api.UpdatePolicyAsync(_provider.Id, patch);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _feedback.Error(AccountRules.ErrorText(e));
        }
        finally
        {
            _saving = false;
            Build();
        }
    }
}
