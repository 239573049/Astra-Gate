using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// The admin password row (web <c>PasswordRow</c>): what <c>astra-server set-password</c> sets; remote admin and LAN access need it.
/// The server refuses changing it through a tunnel or from another device, so the button is not offered there. Choosing it opens an inline
/// editor under the row (at least 8 characters).
/// </summary>
public sealed class PasswordRows : IRowSet
{
    private readonly Func<string, Task<bool>> _save;
    private readonly SettingsRow _row = new();
    private readonly Control _editorLine;
    private readonly ArcButton _change;
    private readonly Input _field;
    private readonly ArcButton _saveButton;
    private bool _editing;
    private bool _passwordSet;
    private bool _hideChange;
    private string _text = "";

    /// <summary>Creates the rows; <paramref name="save"/> sends the password and says whether it was stored.</summary>
    public PasswordRows(Func<string, Task<bool>> save)
    {
        _save = save;
        _change = Ui.Button(null, () => SetEditing(true));
        _row.Trailing.Add(_change);

        _field = Ui.Secret(Loc.T("settings.remote.passwordNew"), "", t => { _text = t; UpdateSave(); }, 256, Loc.T("settings.remote.passwordNew"));
        _saveButton = Ui.Button(Loc.T("common.save"), () => _ = SaveAsync(), ButtonVariant.Primary);
        var cancel = Ui.Button(Loc.T("common.cancel"), () => SetEditing(false), ButtonVariant.Ghost);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _field, cancel, _saveButton } };
        _editorLine = new Border { Padding = new Thickness(16, 8), Child = line };
        Texts();
    }

    /// <inheritdoc />
    public IReadOnlyList<Control> Rows => _editing ? [_row, _editorLine] : [_row];

    /// <inheritdoc />
    public event Action? RowsChanged;

    /// <summary>Takes the server's state: whether a password exists, and whether the change button must stay away (another device / tunnel).</summary>
    public void Update(bool passwordSet, bool hideChange)
    {
        _passwordSet = passwordSet;
        _hideChange = hideChange;
        Texts();
        if (hideChange && _editing) SetEditing(false);
    }

    private void Texts()
    {
        _row.Label = Loc.T("settings.remote.password");
        _row.Detail = Loc.T(_passwordSet ? "settings.remote.passwordSet" : "settings.remote.passwordMissing");
        _change.Content = Loc.T(_passwordSet ? "settings.remote.passwordChange" : "settings.remote.passwordSetAction");
        _change.IsVisible = !_editing && !_hideChange;
        UpdateSave();
    }

    private void UpdateSave() => _saveButton.IsEnabled = _text.Length >= Core.Settings.ListenLogic.MinPasswordLength;

    private void SetEditing(bool on)
    {
        if (_editing == on) return;
        _editing = on;
        if (!on)
        {
            _text = "";
            _field.Text = "";
        }
        Texts();
        RowsChanged?.Invoke();
        // web: the inline password field has autoFocus.
        if (on) Avalonia.Threading.Dispatcher.UIThread.Post(() => _field.Focus(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task SaveAsync()
    {
        _saveButton.IsLoading = true;
        try
        {
            if (await _save(_text)) SetEditing(false);
        }
        finally
        {
            _saveButton.IsLoading = false;
        }
    }
}
