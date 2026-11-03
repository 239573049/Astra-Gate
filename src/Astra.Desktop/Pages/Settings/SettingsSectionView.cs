using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// A Settings category drawn from <c>/api/settings</c> (the parts of the web's <c>GeneralSettings</c> the desktop splits into categories).
/// The groups are drawn once the settings arrive and then only updated in place, so a number being edited is not lost to a refresh
/// (see <see cref="LiveView"/>): controls register an updater, and <see cref="Update"/> / <see cref="Refreshed"/> move the rest.
/// </summary>
public abstract class SettingsSectionView : LiveView
{
    private readonly List<Action<SettingsDto>> _updaters = [];
    private ContentControl _host = null!;
    private bool _groupsBuilt;

    /// <summary>Creates the view over the Settings page.</summary>
    protected SettingsSectionView(SettingsPageViewModel page) : base(page.Context!)
    {
        Page = page;
        Api = page.Api;
        Watch(Api.Settings);
    }

    /// <summary>The Settings page.</summary>
    protected SettingsPageViewModel Page { get; }

    /// <summary>The page's queries and writes.</summary>
    protected SettingsApi Api { get; }

    /// <summary>The settings the groups show (null until they arrived).</summary>
    protected SettingsDto? Last { get; private set; }

    /// <inheritdoc />
    protected sealed override void Build()
    {
        _updaters.Clear();
        _groupsBuilt = false;
        Last = null;
        _host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        BuildFrame(root, _host);
        Content = root;
    }

    /// <summary>Places the settings-driven part (<paramref name="settingsHost"/>) and anything that does not need the settings.</summary>
    protected virtual void BuildFrame(StackPanel root, Control settingsHost) => root.Children.Add(settingsHost);

    /// <summary>Draws the groups from the first settings that arrived.</summary>
    protected abstract void BuildGroups(SettingsDto s, StackPanel stack);

    /// <summary>New settings after the groups were drawn (<paramref name="previous"/> is what they showed).</summary>
    protected virtual void Update(SettingsDto previous, SettingsDto s) { }

    /// <summary>After every refresh with settings, once the updaters ran.</summary>
    protected virtual void Refreshed(SettingsDto s) { }

    /// <summary>Every refresh, settings or not (the parts that do not depend on them).</summary>
    protected virtual void RefreshFrame() { }

    /// <inheritdoc />
    protected sealed override void Refresh()
    {
        RefreshFrame();
        var s = Api.Settings.Data;
        if (s is null)
        {
            if (!_groupsBuilt)
                _host.Content = Api.Settings.IsError
                    ? Ui.ErrorText(UiHost.ErrorText(Api.Settings.Error!))
                    : new Border { Padding = new Thickness(0, 32), Child = Ui.Spinner() };
            return;
        }
        if (!_groupsBuilt)
        {
            Last = s;
            var stack = new StackPanel();
            BuildGroups(s, stack);
            _host.Content = stack;
            _groupsBuilt = true;
        }
        else Update(Last!, s);
        Last = s;
        foreach (var update in _updaters) update(s);
        Refreshed(s);
    }

    /// <summary>Saves a settings patch (errors are toasted).</summary>
    protected async Task PatchAsync(JsonObject patch) => await Host.TryAsync(() => Api.PatchSettingsAsync(patch));

    /// <summary>Keeps something in step with the settings on every refresh.</summary>
    protected void OnSettings(Action<SettingsDto> update) => _updaters.Add(update);

    /// <summary>A number field that saves on commit and shows the saved value after each refresh.</summary>
    protected DeferredNumberField Number(string label, Func<SettingsDto, decimal> value, Func<decimal, JsonObject> patch, string? suffix, decimal step = 1,
        Func<decimal, Task>? commit = null)
    {
        var field = new DeferredNumberField(label, value(Last!), v => _ = commit is null ? PatchAsync(patch(v)) : commit(v), suffix, step);
        _updaters.Add(s => field.SetSaved(value(s)));
        return field;
    }

    /// <summary>A switch that asks <paramref name="toggle"/> to change and shows the saved value after each refresh.</summary>
    protected ControlledSwitch Switch(string label, Func<SettingsDto, bool> value, Func<bool, Task> toggle)
    {
        var sw = new ControlledSwitch { IsChecked = value(Last!) };
        Avalonia.Automation.AutomationProperties.SetName(sw, label);
        sw.ToggleRequested += on => _ = toggle(on);
        _updaters.Add(s => sw.SetValue(value(s)));
        return sw;
    }
}
