using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>
/// The create and settings sheets of the token list (web <c>CreateTokenSheet</c> and <c>TokenSettingsSheet</c>): modal sheets (the
/// kit's <see cref="Sheet"/>, the web's centered dialog) that keep a draft while they are open and go through
/// <see cref="TokenActions"/> on submit, staying open on an error.
/// </summary>
public static class TokenSheets
{
    /// <summary>"New token": a name and the direct provider (none by default).</summary>
    public static Sheet ShowCreate(Visual owner, TokensPage page, IReadOnlyList<ProviderDto> providers)
    {
        var name = new Input { Label = Loc.T("tokens.name"), MaxLength = 64, Placeholder = Loc.T("tokens.namePlaceholder") };
        var options = new List<(string, string)> { ("", Loc.T("tokens.directNone")) };
        options.AddRange(providers.Where(p => p.Enabled).Select(p => (p.Id, p.Name)));
        var providerId = "";
        var provider = Pick.Select(options, "", v => providerId = v, Loc.T("tokens.directProvider"));
        provider.Label = Loc.T("tokens.directProvider");

        var cancel = new ArcButton { Content = Loc.T("common.cancel"), Variant = ButtonVariant.Secondary };
        var create = AccentButton.Create(Loc.T("common.create"));
        create.IsEnabled = false;
        var sheet = new Sheet
        {
            Title = Loc.T("tokens.createTitle"),
            Content = new StackPanel { Spacing = 16, Children = { name, provider } },
            Footer = [cancel, create],
        };
        name.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        cancel.Click += (_, _) => sheet.Close();
        create.Click += async (_, _) =>
        {
            create.IsLoading = true;
            var made = await page.Actions.CreateAsync(name.Text ?? "", providerId);
            create.IsLoading = false;
            if (made is not null) sheet.Close();
        };
        _ = sheet.ShowAsync(owner);
        return sheet;
    }

    /// <summary>A token's settings: name, provider pool, usage caps, request-rate cap and allowed clients.</summary>
    public static Sheet ShowSettings(Visual owner, TokensPage page, TokenDto token, IReadOnlyList<ProviderDto> providers)
    {
        var draft = TokenSettingsDraft.Of(token);
        var health = token.Pool?.Members.ToDictionary(m => m.ProviderId, m => m.Health) ?? [];
        var zone = TimeSpan.FromMinutes(page.Now.OffsetMinutes);

        var save = AccentButton.Create(Loc.T("common.save"));
        save.IsEnabled = draft.CanSave;
        var cancel = new ArcButton { Content = Loc.T("common.cancel"), Variant = ButtonVariant.Secondary };
        FieldBlock? limitsField = null;

        void Edited(TokenSettingsDraft next)
        {
            draft = next;
            save.IsEnabled = draft.CanSave;
            if (limitsField is not null) limitsField.Error = BudgetRules.HasDuplicate(draft.Budgets) ? Loc.T("tokens.limitDuplicate") : null;
        }

        var name = new Input { Label = Loc.T("tokens.name"), MaxLength = 64, Text = draft.Name };
        name.TextChanged += (_, _) => Edited(draft with { Name = name.Text ?? "" });

        var pool = new TokenPoolEditor(draft.Pool,
            new PoolEditorContext(providers, health, page.Accounts,
                (state, until) => PoolDrafts.HealthLabel(state, until, page.Clock, zone, Loc.Language, (k, a) => Loc.T(k, a))),
            p => Edited(draft with { Pool = p }));
        var budgets = new BudgetsEditor(draft.Budgets, b => Edited(draft with { Budgets = b }));
        limitsField = new FieldBlock(Loc.T("tokens.limits"), budgets, Loc.T("tokens.limitsHint"));

        var rpm = new NumberBox { Value = draft.Rpm is { } r ? (decimal)r : null, Minimum = 1, Placeholder = Loc.T("tokens.unlimited") };
        Avalonia.Automation.AutomationProperties.SetName(rpm, Loc.T("tokens.rpm"));
        rpm.ValueEdited += v => Edited(draft with { Rpm = v is { } d ? (double)d : null });

        var form = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                name,
                new FieldBlock(Loc.T("tokens.pool.title"), pool, Loc.T("tokens.pool.hint")),
                limitsField,
                new FieldBlock(Loc.T("tokens.rpm"), rpm, Loc.T("tokens.rpmHint")),
                new FieldBlock(Loc.T("tokens.allowedClients"), AllowedClients(draft, d => Edited(d)), Loc.T("tokens.allowedClientsHint")),
            },
        };

        var sheet = new Sheet { Title = Loc.T("tokens.settingsTitle", ("name", token.Name)), Content = form, Footer = [cancel, save] };
        cancel.Click += (_, _) => sheet.Close();
        save.Click += async (_, _) =>
        {
            save.IsLoading = true;
            var ok = await page.Actions.UpdateAsync(token.Id, draft.ToPatch(providers.Select(p => p.Id)), Loc.T("common.saved"));
            save.IsLoading = false;
            if (ok) sheet.Close();
        };
        _ = sheet.ShowAsync(owner);
        return sheet;
    }

    /// <summary>"Any client" or "Only these" with a chip per client; kinds outside the catalog (plugin agents) stay selectable once chosen.</summary>
    private static Control AllowedClients(TokenSettingsDraft start, Action<TokenSettingsDraft> edited)
    {
        var current = start;
        var host = new StackPanel { Spacing = 8 };
        ChipGroup? chips = null;

        void Update(TokenSettingsDraft next)
        {
            current = next;
            edited(next);
        }

        Control Chips()
        {
            var choices = TokenSettingsDraft.ClientChoices(ClientCatalog.Order, current.Allowed);
            chips = new ChipGroup
            {
                Label = Loc.T("tokens.allowedClients"),
                ItemsSource = choices,
                TextSelector = k => ClientCatalog.Name((string)k!),
                ValueSelector = k => k,
            };
            foreach (var kind in current.Allowed)
                if (choices.IndexOf(kind) is var at and >= 0) chips.Selection.Select(at);
            chips.SelectionChanged += (_, _) => Update(current with { Allowed = chips.SelectedValues.Cast<string>().ToList() });
            return chips;
        }

        var mode = Pick.Segmented(
            [new SegmentedOption("any", Loc.T("tokens.allowedClientsAny")), new SegmentedOption("only", Loc.T("tokens.allowedClientsOnly"))],
            current.Mode == ClientsMode.Any ? "any" : "only",
            v =>
            {
                Update(current with { Mode = v == "any" ? ClientsMode.Any : ClientsMode.Only });
                if (host.Children.Count > 1) host.Children.RemoveAt(1);
                if (v == "only") host.Children.Add(Chips());
            }, Loc.T("tokens.allowedClients"));
        mode.HorizontalAlignment = HorizontalAlignment.Left;
        host.Children.Add(mode);
        if (current.Mode == ClientsMode.Only) host.Children.Add(Chips());
        return host;
    }
}
