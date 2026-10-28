using Astra.Core.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Avalonia.Controls;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// Client-specific options (<c>extras.options</c>), rendered from the descriptors the server reports for the client (web
/// <c>ClientOptionsGroup</c>; AGENTS.md linked update 6). A control factory keyed on the descriptor's <c>type</c> — "bool" a switch, "int" a
/// digits-only input, "enum" a select of its choices — with the label / hint / choice texts from <c>clients.option.&lt;key&gt;[.hint]</c> and
/// <c>clients.optionChoice.&lt;choice&gt;</c>. No reflection: the descriptor is data. Edits go to <c>onChange</c> as a whole new map
/// (<see cref="ClientExtras.SetOption"/>), so the page decides when they are saved.
/// </summary>
public static class ClientOptionsGroup
{
    /// <summary>The group for <paramref name="descriptors"/> showing <paramref name="values"/>.</summary>
    public static SettingsGroup Create(IReadOnlyList<ClientOptionDescriptor> descriptors, IReadOnlyDictionary<string, OptionValue> values,
        Action<Dictionary<string, OptionValue>> onChange, bool enabled)
    {
        // The rows read the latest map, not the one they were built with: several edits may happen before the page redraws.
        var current = values;
        void Set(string key, OptionValue? value)
        {
            var next = ClientExtras.SetOption(current, key, value);
            current = next;
            onChange(next);
        }

        var group = new SettingsGroup
        {
            Title = Loc.T("clients.options.title"),
            Footer = enabled ? Loc.T("clients.options.footerEnabled") : Loc.T("clients.options.footer"),
        };
        foreach (var d in descriptors)
        {
            var label = Loc.T("clients.option." + d.Key);
            var row = new SettingsRow { Label = label, Detail = Loc.T("clients.option." + d.Key + ".hint") };
            if (Control(d, label, values.GetValueOrDefault(d.Key), Set) is { } control) row.Trailing.Add(control);
            group.Rows.Add(row);
        }
        return group;
    }

    /// <summary>The editor of one descriptor (null for a type this client does not know: the row still names the option).</summary>
    public static Control? Control(ClientOptionDescriptor d, string label, OptionValue? value, Action<string, OptionValue?> set)
    {
        switch (d.Type)
        {
            case "bool":
            {
                var toggle = new Switch { IsChecked = value is OptionValue.Bool { Value: true } };
                Avalonia.Automation.AutomationProperties.SetName(toggle, label);
                toggle.IsCheckedChanged += (_, _) => set(d.Key, new OptionValue.Bool(toggle.IsChecked == true));
                return toggle;
            }
            case "int":
            {
                var input = new Input
                {
                    Width = 144,
                    Placeholder = Loc.T("clients.options.unset"),
                    Text = value is OptionValue.Int i ? i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                };
                input.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                Avalonia.Automation.AutomationProperties.SetName(input, label);
                input.TextChanged += (_, _) =>
                {
                    var n = ClientExtras.ParseIntOption(input.Text);
                    set(d.Key, n is { } v ? new OptionValue.Int(v) : null);
                };
                return input;
            }
            case "enum":
            {
                var options = (d.Choices ?? []).Select(c => (c, Loc.T("clients.optionChoice." + c), false));
                return ClientUi.Select(options, value is OptionValue.Choice c ? c.Value : "default",
                    v => set(d.Key, new OptionValue.Choice(v)), label, width: 144);
            }
            default:
                return null;
        }
    }
}
