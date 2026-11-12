using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// The v2 part of the desktop Plugins page (the web's PluginsV2Section): the update reminder for v1 plugins that have a v2
/// version, and the v2 plugins with their status, permissions and settings (design/plugin-v2 01 §6, 07 §3).
/// </summary>
public sealed class PluginsV2Section : UserControl
{
    private readonly PluginV2Api _api;
    private readonly IFeedback _feedback;
    private readonly ApiClient _client;
    private readonly StackPanel _column = new() { Spacing = 16, Margin = new Thickness(0, 0, 0, 16) };
    private IDisposable? _listWatch, _successorWatch;

    public PluginsV2Section(PluginV2Api api, IFeedback feedback, ApiClient client)
    {
        _api = api;
        _feedback = feedback;
        _client = client;
        Content = _column;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _listWatch = _api.List.Activate();
        _successorWatch = _api.Successors.Activate();
        _api.List.PropertyChanged += Changed;
        _api.Successors.PropertyChanged += Changed;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.List.PropertyChanged -= Changed;
        _api.Successors.PropertyChanged -= Changed;
        _listWatch?.Dispose();
        _successorWatch?.Dispose();
    }

    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Data") Dispatcher.UIThread.Post(Render);
    }

    private void Render()
    {
        _column.Children.Clear();
        if (_api.Successors.Data is { Count: > 0 } successors) _column.Children.Add(Successors(successors));
        var plugins = _api.List.Data?.Plugins ?? [];
        if (plugins.Count == 0) return;
        var group = new SettingsGroup { Title = Loc.T("pluginUi.v2Title") };
        foreach (var p in plugins) group.Rows.Add(Row(p));
        _column.Children.Add(group);
        // the component gallery (06 §2), for plugin authors: offered while a plugin is loaded from a folder
        if (plugins.Any(p => p.Dev))
        {
            var gallery = Ui.Button(Loc.T("pluginUi.gallery.title"), () => PluginShell.Navigate("/plugins/dev/gallery"), ButtonVariant.Ghost, LucideIconKind.LayoutGrid);
            gallery.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            _column.Children.Add(gallery);
        }
    }

    private Control Row(PluginV2Dto p)
    {
        var status = p.Running ? Loc.T("pluginUi.running") : p.Problem ?? (p.Enabled ? Loc.T("pluginUi.idle") : Loc.T("pluginUi.off"));
        var row = new SettingsRow
        {
            Label = $"{p.Name}  {p.Version}",
            Detail = (p.Description is { } d ? d + " · " : "") + status + (p.LastError is { } err ? " · " + err : ""),
            Icon = new LucideIcon { Kind = PluginShell.IconKind(p.Icon), Size = 16 },
        };
        if (p.Sensitive.Count > 0) row.Trailing.Add(KitBadge.Create(Loc.T("pluginUi.sensitive"), Tone.Orange, string.Join(", ", p.Sensitive)));
        if (p.Missing.Count > 0) row.Trailing.Add(Ui.Button(Loc.T("pluginUi.grant"), () => _ = GrantAsync(p), ButtonVariant.Primary, LucideIconKind.ShieldCheck));
        if (p.Configuration is not null || p.Contributes.Settings.Count > 0)
            row.Trailing.Add(Ui.Button(Loc.T("pluginUi.settings"), () => _ = SettingsAsync(p), icon: LucideIconKind.Settings2));
        // the console's row menu: permissions, the plugin's commands, and going back to v1 after an upgrade
        var more = new List<Ui.MenuItem> { new(Loc.T("pluginUi.permissions"), LucideIconKind.ShieldCheck, () => _ = GrantAsync(p)) };
        foreach (var (c, i) in p.Contributes.Commands.Where(c => c.Plugin == p.Id).Select((c, i) => (c, i)))
            more.Add(new(c.Title, PluginShell.IconKind(c.Icon), () => _ = GuardAsync(async () =>
            {
                var result = await _api.RunCommandAsync(p.Id, c.Id, null);
                _feedback.Success(result is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 ? text : Loc.T("pluginUi.commandRan", ("name", c.Title)));
            }), SeparatorBefore: i == 0));
        if (p.Replaces.Count > 0)
            more.Add(new(Loc.T("pluginUi.rollback"), LucideIconKind.Undo2, () => _ = GuardAsync(async () =>
            {
                await _api.RollbackAsync(p.Id);
                _feedback.Info(Loc.T("pluginUi.rollback"), p.Name);
            }), SeparatorBefore: true));
        row.Trailing.Add(Ui.Menu(Loc.T("common.more"), LucideIconKind.Ellipsis, more));
        var toggle = new ControlledSwitch { IsChecked = p.Enabled };
        toggle.ToggleRequested += on => _ = GuardAsync(async () =>
        {
            await _client.PatchAsync<JsonObject, PluginsDto>("/api/plugins" + ApiClient.Qs(("spec", p.Spec)), new JsonObject { ["enabled"] = on });
            toggle.SetValue(on);
            await _api.List.RefetchAsync();
        });
        row.Trailing.Add(toggle);
        return row;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _feedback.Error(e.Message);
        }
    }

    private static Control PermissionList(IEnumerable<string> permissions, IReadOnlyCollection<string> sensitive)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var p in permissions)
        {
            var icon = new LucideIcon { Kind = sensitive.Contains(p) ? LucideIconKind.ShieldAlert : LucideIconKind.ShieldCheck, Size = 14 };
            icon.Token(TemplatedControl.ForegroundProperty, sensitive.Contains(p) ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.Success);
            list.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, Ui.Note(PluginShell.PermissionText(p) + "   " + p, ArcDesignTokenKind.Foreground, 13) } });
        }
        return list;
    }

    private async Task GrantAsync(PluginV2Dto p)
    {
        var ok = false;
        var sheet = new Sheet { Title = Loc.T("pluginUi.grantTitle", ("name", p.Name)), Content = new StackPanel { Spacing = 12, Children = { Ui.Note(Loc.T("pluginUi.grantDetail")), PermissionList(p.Missing, p.Sensitive) } } };
        sheet.Footer =
        [
            Ui.Button(Loc.T("common.cancel"), () => sheet.Close()),
            Ui.Button(Loc.T("pluginUi.allow"), () =>
            {
                ok = true;
                sheet.Close();
            }, ButtonVariant.Primary),
        ];
        await sheet.ShowAsync(this);
        if (ok) await GuardAsync(() => _api.GrantAsync(p.Id, p.Missing));
    }

    /// <summary>The settings form from the configuration schema (02 §9), and the plugin's own settings view when it has one.</summary>
    private async Task SettingsAsync(PluginV2Dto p)
    {
        var values = (JsonObject)p.Settings.DeepClone();
        var secrets = new JsonObject();
        var form = new StackPanel { Spacing = 14 };
        foreach (var f in (p.Configuration?["properties"] as JsonArray ?? []).OfType<JsonObject>()) form.Children.Add(Field(f, values, secrets, p.SecretsSet));
        PluginViewHost? view = null;
        if (p.Contributes.Settings.FirstOrDefault() is { } settingsView)
        {
            view = new PluginViewHost(_api, new ViewTarget(p.Id, settingsView.View), _feedback, PluginShell.Navigate, surface: "sheet");
            form.Children.Add(view);
        }
        var save = false;
        var sheet = new Sheet { Title = Loc.T("pluginUi.settingsTitle", ("name", p.Name)), SheetWidth = 560, Content = new ScrollViewer { Content = form, MaxHeight = 600 } };
        sheet.Footer =
        [
            Ui.Button(Loc.T("common.cancel"), () => sheet.Close()),
            Ui.Button(Loc.T("pluginUi.save"), () =>
            {
                save = true;
                sheet.Close();
            }, ButtonVariant.Primary),
        ];
        await sheet.ShowAsync(this);
        if (view is not null) await view.CloseAsync();
        if (!save || p.Configuration is null) return;
        await GuardAsync(async () =>
        {
            await _api.SaveSettingsAsync(p.Id, values, secrets);
            _feedback.Success(Loc.T("pluginUi.saved"));
        });
    }

    private static Control Field(JsonObject f, JsonObject values, JsonObject secrets, IReadOnlyList<string> secretsSet)
    {
        var name = f["name"]?.ToString() ?? "";
        var type = f["type"]?.ToString() ?? "string";
        Control editor;
        if (f["secret"] is JsonValue s && s.GetValueKind() == JsonValueKind.True)
            editor = Ui.Secret(name, "", v => secrets[name] = v.Length == 0 ? null : v, 320, secretsSet.Contains(name) ? Loc.T("pluginUi.secretSet") : null);
        else if (type == "boolean")
        {
            var toggle = new ControlledSwitch { IsChecked = values[name] is JsonValue b && b.GetValueKind() == JsonValueKind.True };
            toggle.ToggleRequested += on =>
            {
                toggle.SetValue(on);
                values[name] = on;
            };
            editor = toggle;
        }
        else if (f["enum"] is JsonArray options)
        {
            var titles = f["enumTitles"] as JsonArray;
            var select = new ControlledSelect(options.Select((o, i) => (o?.ToString() ?? "", titles?[i]?.ToString() ?? o?.ToString() ?? "")), values[name]?.ToString(), 240);
            select.Chose += v =>
            {
                select.SetValue(v);
                values[name] = v;
            };
            editor = select;
        }
        else if (type is "number" or "integer")
        {
            var field = new NumberField { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, MaximumFractionDigits = type == "integer" ? 0 : 6 };
            if (values[name] is JsonValue v && v.TryGetValue<double>(out var d)) field.Value = (decimal)d;
            if (f["minimum"] is JsonValue mn && mn.TryGetValue<double>(out var min)) field.Minimum = (decimal)min;
            if (f["maximum"] is JsonValue mx && mx.TryGetValue<double>(out var max)) field.Maximum = (decimal)max;
            field.ValueChanged += (_, _) => values[name] = type == "integer" ? JsonValue.Create((long)field.Value) : JsonValue.Create((double)field.Value);
            editor = field;
        }
        else if (type == "array" && f["itemsType"]?.ToString() == "string")
        {
            var tags = new TagInput { Value = (values[name] as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToList() };
            tags.ValueChanged += (_, _) => values[name] = new JsonArray([.. tags.Value.Select(x => (JsonNode?)JsonValue.Create(x))]);
            editor = tags;
        }
        else if (type is "array" or "object")
        {
            var area = new Textarea { Text = values[name]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? (type == "array" ? "[]" : "{}"), AcceptsReturn = true, MinHeight = 100 };
            area.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            area.TextChanged += (_, _) =>
            {
                try
                {
                    values[name] = JsonNode.Parse(area.Text ?? "");
                }
                catch (JsonException)
                {
                }
            };
            editor = area;
        }
        else editor = Ui.Field(name, values[name]?.ToString() ?? "", v => values[name] = v, 320);
        return Ui.Labeled(f["title"]?.ToString() ?? name, editor, f["description"]?.ToString());
    }

    // ---- the update reminder

    private Control Successors(List<PluginSuccessorDto> list)
    {
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(KitStyle.Text(Loc.T("pluginUi.successors", ("count", list.Count)), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.Medium));
        foreach (var s in list)
        {
            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(Ui.Button(Loc.T("pluginUi.upgrade"), () => _ = UpgradeAsync(s), ButtonVariant.Primary));
            buttons.Children.Add(Ui.Button(Loc.T("pluginUi.dismiss"), () => _ = GuardAsync(() => _api.DismissSuccessorAsync(s.Spec, s.Version)), ButtonVariant.Ghost));
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(Ui.Note($"{s.V1Name} → {s.Name} v{s.Version}", ArcDesignTokenKind.Foreground, 13));
            column.Children.Add(row);
        }
        var border = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = column };
        return border.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Accent);
    }

    private async Task UpgradeAsync(PluginSuccessorDto s)
    {
        var go = false;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Ui.Note(Loc.T("pluginUi.upgradeDetail")));
        if (s.Notes is { } notes) body.Children.Add(Ui.Note(notes, ArcDesignTokenKind.Foreground, 13));
        body.Children.Add(KitStyle.Text(Loc.T("pluginUi.upgradePermissions"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        body.Children.Add(PermissionList(s.Permissions, s.Sensitive));
        body.Children.Add(Ui.Note(Loc.T("pluginUi.upgradeSafety")));
        var sheet = new Sheet { Title = Loc.T("pluginUi.upgradeTitle", ("name", s.V1Name)), Content = body };
        sheet.Footer =
        [
            Ui.Button(Loc.T("common.cancel"), () => sheet.Close()),
            Ui.Button(Loc.T("pluginUi.upgradeConfirm"), () =>
            {
                go = true;
                sheet.Close();
            }, ButtonVariant.Primary),
        ];
        await sheet.ShowAsync(this);
        if (!go) return;
        await GuardAsync(async () =>
        {
            await _api.UpgradeAsync(s.Spec, s.Permissions);
            _feedback.Info(Loc.T("pluginUi.upgrade"), s.Name);
        });
    }
}
