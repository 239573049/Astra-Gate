using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia.Controls;

namespace Astra.Desktop.Pages.Settings;

/// <summary>Settings › Models: provider-qualified model ids (issue #21) and the effort ↔ thinking-budget table.</summary>
public sealed class ModelSettingsView : SettingsSectionView
{
    /// <summary>Creates the view.</summary>
    public ModelSettingsView(SettingsPageViewModel page) : base(page) { }

    /// <inheritdoc />
    protected override void BuildGroups(SettingsDto s, StackPanel stack)
    {
        // provider-qualified model ids (web SettingsPage QualifiedModelsSettings): the mode applies at once, the separator is saved with its button
        var separatorText = s.ModelProviderSeparator;
        Avalonia.Controls.Button? saveSeparator = null;
        var separatorField = Ui.Field(Loc.T("settings.qualifiedModels.separator"), s.ModelProviderSeparator, t =>
        {
            separatorText = t;
            if (saveSeparator is not null) saveSeparator.IsEnabled = separatorText != Last!.ModelProviderSeparator;
        }, 96, mono: true);
        separatorField.MaxLength = 8;
        var saveSeparatorButton = Ui.Button(Loc.T("common.save"), () => _ = PatchAsync(JsonPatch.Of(("modelProviderSeparator", separatorText))), enabled: false);
        saveSeparator = saveSeparatorButton;
        OnSettings(x =>
        {
            if (separatorField.Text != x.ModelProviderSeparator) separatorField.Text = x.ModelProviderSeparator;
            separatorText = x.ModelProviderSeparator;
            saveSeparatorButton.IsEnabled = false;
            separatorField.IsEnabled = x.ProviderQualifiedModels;
        });
        separatorField.IsEnabled = s.ProviderQualifiedModels;
        // off / only ids several providers serve / every id: one choice over providerQualifiedModels + modelQualifyScope
        static string ModeOf(SettingsDto x) => x.ProviderQualifiedModels ? x.ModelQualifyScope : "off";
        var mode = new ControlledSelect(new[] { "off", "shared", "all" }.Select(m => (m, Loc.T("settings.qualifiedModels.mode." + m))), ModeOf(s), 200);
        Avalonia.Automation.AutomationProperties.SetName(mode, Loc.T("settings.qualifiedModels.mode"));
        mode.Chose += v => _ = PatchAsync(v == "off"
            ? JsonPatch.Of(("providerQualifiedModels", false))
            : JsonPatch.Of(("providerQualifiedModels", true), ("modelQualifyScope", v)));
        OnSettings(x => mode.SetValue(ModeOf(x)));
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.qualifiedModels"), null,
            RowBuilder.Hinted(Loc.T("settings.qualifiedModels.mode"), Loc.T("settings.qualifiedModels.modeHint"), mode),
            RowBuilder.Hinted(Loc.T("settings.qualifiedModels.separator"), Loc.T("settings.qualifiedModels.separatorHint"), separatorField, saveSeparatorButton))
            .WithHint(Loc.T("settings.qualifiedModels.footer")));

        // effort budgets
        var effort = RowBuilder.Group(Loc.T("settings.effort"), null).WithHint(Loc.T("settings.effortHint"));
        foreach (var level in GeneralLogic.Efforts)
        {
            var l = level;
            effort.Rows.Add(RowBuilder.Row(Loc.T("settings.effort." + l), null,
                Number(Loc.T("settings.effort." + l), x => GeneralLogic.Budget(x.EffortBudgets, l), v => GeneralLogic.EffortPatch(Last!.EffortBudgets, l, (int?)v), " tokens", 1024)));
        }
        stack.Children.Add(effort);
    }
}

/// <summary>Settings › Network: the outbound proxy and the plugin market's registry.</summary>
public sealed class NetworkSettingsView : SettingsSectionView
{
    private ProxyGroup _proxy = null!;
    private MarketGroup _market = null!;

    /// <summary>Creates the view.</summary>
    public NetworkSettingsView(SettingsPageViewModel page) : base(page) { }

    /// <inheritdoc />
    protected override void BuildGroups(SettingsDto s, StackPanel stack)
    {
        _proxy = new ProxyGroup(Host, Api, s);
        stack.Children.Add(_proxy.Group);
        _market = new MarketGroup(Host, Api, s);
        stack.Children.Add(_market.Group);
    }

    /// <inheritdoc />
    protected override void Update(SettingsDto previous, SettingsDto s)
    {
        _proxy.Update(previous, s);
        _market.Update(previous, s);
    }
}

/// <summary>Settings › Usage: background quota refresh intervals and the time zone token limits reset in.</summary>
public sealed class UsageSettingsView : SettingsSectionView
{
    /// <summary>Creates the view.</summary>
    public UsageSettingsView(SettingsPageViewModel page) : base(page) { }

    /// <inheritdoc />
    protected override void BuildGroups(SettingsDto s, StackPanel stack)
    {
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.quota"), null,
            RowBuilder.Hinted(Loc.T("settings.quota.autoInterval"), Loc.T("settings.quota.autoIntervalHint"),
                Number(Loc.T("settings.quota.autoInterval"), x => x.QuotaAutoIntervalMinutes, v => JsonPatch.Of(("quotaAutoIntervalMinutes", (int?)v)), $" {Loc.T("settings.minutes")}",
                    commit: async v =>
                    {
                        // Providers show their effective interval: refresh them after the global one changes.
                        if (await Host.TryAsync(() => Api.PatchSettingsAsync(JsonPatch.Of(("quotaAutoIntervalMinutes", (int?)v)))) is not null) Api.RefreshProviders();
                    })),
            RowBuilder.Hinted(Loc.T("settings.quota.subscriptionInterval"), Loc.T("settings.quota.subscriptionIntervalHint"),
                Number(Loc.T("settings.quota.subscriptionInterval"), x => x.SubscriptionQuotaIntervalMinutes, v => JsonPatch.Of(("subscriptionQuotaIntervalMinutes", (int?)v)),
                    $" {Loc.T("settings.minutes")}"))));

        var zones = new ControlledSelect([("", Loc.T("settings.usageTimeZone.system", ("zone", s.EffectiveUsageTimeZone))), .. GeneralLogic.ZoneOptions(s).Select(z => (z, z))], s.UsageTimeZone ?? "", 224);
        Avalonia.Automation.AutomationProperties.SetName(zones, Loc.T("settings.usageTimeZone"));
        zones.Chose += v => _ = PatchAsync(JsonPatch.Of(("usageTimeZone", v == "" ? null : v)));
        OnSettings(x => zones.SetValue(x.UsageTimeZone ?? ""));
        stack.Children.Add(RowBuilder.Group(null, null, RowBuilder.Hinted(Loc.T("settings.usageTimeZone"), Loc.T("settings.usageTimeZone.hint"), zones)));

        // display currency (web SettingsPage CurrencySettings): manual rates, nothing fetched; money stays USD underneath
        var currency = new ControlledSelect(CurrencyForm.Options(), s.DisplayCurrency, 200);
        Avalonia.Automation.AutomationProperties.SetName(currency, Loc.T("settings.currency.display"));
        currency.Chose += v =>
        {
            if (CurrencyForm.HasRate(v, Last!.CurrencyRates)) _ = PatchAsync(JsonPatch.Of(("displayCurrency", v)));
            else
            {
                Context.Feedback.Error(Loc.T("settings.currency.needRate", ("code", v)));
                currency.SetValue(Last.DisplayCurrency);
            }
        };
        OnSettings(x => currency.SetValue(x.DisplayCurrency));
        var ratesText = CurrencyForm.Format(s.CurrencyRates);
        Avalonia.Controls.Button? saveRates = null;
        var ratesField = Ui.Field(Loc.T("settings.currency.rates"), ratesText, t =>
        {
            ratesText = t;
            if (saveRates is not null) saveRates.IsEnabled = ratesText != CurrencyForm.Format(Last!.CurrencyRates);
        }, 288, mono: true, placeholder: "CNY=7.2, EUR=0.92");
        var saveRatesButton = Ui.Button(Loc.T("common.save"), () =>
        {
            if (CurrencyForm.Parse(ratesText, out var bad) is not { } table)
            {
                Context.Feedback.Error(Loc.T("settings.currency.badRate", ("entry", bad ?? "")));
                return;
            }
            var patch = new JsonObject();
            foreach (var (code, rate) in table) patch[code] = rate;
            // The display currency falls back to USD together with its rate.
            _ = PatchAsync(table.ContainsKey(Last!.DisplayCurrency) || Last.DisplayCurrency == "USD"
                ? JsonPatch.Of(("currencyRates", patch))
                : JsonPatch.Of(("displayCurrency", "USD"), ("currencyRates", patch)));
        }, enabled: false);
        saveRates = saveRatesButton;
        OnSettings(x =>
        {
            ratesText = CurrencyForm.Format(x.CurrencyRates);
            if (ratesField.Text != ratesText) ratesField.Text = ratesText;
            saveRatesButton.IsEnabled = false;
        });
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.currency"), null,
            RowBuilder.Hinted(Loc.T("settings.currency.display"), Loc.T("settings.currency.displayHint"), currency),
            RowBuilder.Hinted(Loc.T("settings.currency.rates"), Loc.T("settings.currency.ratesHint"), ratesField, saveRatesButton))
            .WithHint(Loc.T("settings.currency.footer")));
    }
}

/// <summary>Settings › Logging: request/response bodies, how long requests are kept, and conversation content in traces.</summary>
public sealed class LoggingSettingsView : SettingsSectionView
{
    private Components.Kit.SettingsRow _retentionRow = null!;
    private ControlledSwitch _retentionSwitch = null!;
    private DeferredNumberField? _retentionNumber;

    /// <summary>Creates the view.</summary>
    public LoggingSettingsView(SettingsPageViewModel page) : base(page) { }

    /// <inheritdoc />
    protected override void BuildGroups(SettingsDto s, StackPanel stack)
    {
        _retentionNumber = null;
        _retentionSwitch = Switch(Loc.T("settings.requestRetention"), x => x.RequestRetentionDays is not null, on => PatchAsync(JsonPatch.Of(("requestRetentionDays", GeneralLogic.RetentionFor(on)))));
        _retentionRow = RowBuilder.Row(Loc.T("settings.requestRetention"), null);
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.logging"), null,
            RowBuilder.Hinted(Loc.T("settings.debugBodies"), Loc.T("settings.debugBodiesHint"),
                Switch(Loc.T("settings.debugBodies"), x => x.DebugBodies, on => PatchAsync(JsonPatch.Of(("debugBodies", on))))),
            RowBuilder.Row(Loc.T("settings.bodyRetention"), null,
                Number(Loc.T("settings.bodyRetention"), x => x.BodyRetentionDays, v => JsonPatch.Of(("bodyRetentionDays", (int?)v)), $" {Loc.T("settings.days")}")),
            _retentionRow,
            // conversation content in traces (the web shows it as its own group after logging)
            RowBuilder.Hinted(Loc.T("settings.traceContent"), Loc.T("settings.traceContentHint"),
                Switch(Loc.T("settings.traceContent"), x => x.TraceContent, on => PatchAsync(JsonPatch.Of(("traceContent", on)))))));
    }

    /// <inheritdoc />
    protected override void Refreshed(SettingsDto s)
    {
        _retentionRow.Detail = s.RequestRetentionDays is null ? Loc.T("settings.keepForever") : null;
        _retentionRow.Trailing.Clear();
        _retentionRow.Trailing.Add(_retentionSwitch);
        if (s.RequestRetentionDays is { } days)
        {
            _retentionNumber ??= new DeferredNumberField(Loc.T("settings.requestRetention"), days, v => _ = PatchAsync(JsonPatch.Of(("requestRetentionDays", (int?)v))), $" {Loc.T("settings.days")}");
            _retentionNumber.SetSaved(days);
            _retentionRow.Trailing.Add(_retentionNumber);
        }
    }
}
