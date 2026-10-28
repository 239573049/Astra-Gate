using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Platform.Tray;

/// <summary>One native menu entry built for <see cref="MacMenu"/>: its action, if it has one.</summary>
internal sealed record MacMenuEntry(Action? OnSelect);

/// <summary>
/// The Objective-C target object the status item button and every menu item send their action to: a tiny <c>NSObject</c> subclass
/// registered once at run time, whose two methods call back into C# (the menu item's <c>tag</c> names the entry).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacTarget
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionImp(IntPtr self, IntPtr cmd, IntPtr sender);

    // Kept alive for the process: the runtime holds only the function pointer.
    private static readonly ActionImp ClickImp = OnClick;
    private static readonly ActionImp MenuImp = OnMenu;
    private static IntPtr _instance;
    private static long _nextTag;
    private static readonly Dictionary<long, MacMenuEntry> Entries = [];

    /// <summary>The status item was clicked (true = secondary: right click or ⌃-click).</summary>
    public static Action<bool>? Clicked { get; set; }

    public static IntPtr Instance
    {
        get
        {
            if (_instance != IntPtr.Zero) return _instance;
            var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "AstraTrayTarget", IntPtr.Zero);
            ObjC.class_addMethod(cls, ObjC.Sel("astraClick:"), Marshal.GetFunctionPointerForDelegate(ClickImp), "v@:@");
            ObjC.class_addMethod(cls, ObjC.Sel("astraMenu:"), Marshal.GetFunctionPointerForDelegate(MenuImp), "v@:@");
            ObjC.objc_registerClassPair(cls);
            _instance = ObjC.New("AstraTrayTarget");
            return _instance;
        }
    }

    public static long Register(MacMenuEntry entry)
    {
        var tag = ++_nextTag;
        Entries[tag] = entry;
        return tag;
    }

    public static void Forget(IEnumerable<long> tags)
    {
        foreach (var t in tags) Entries.Remove(t);
    }

    private static void OnClick(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            var ev = ObjC.Send(ObjC.App, ObjC.Sel("currentEvent"));
            var type = ev == IntPtr.Zero ? 0 : ObjC.GetLong(ev, ObjC.Sel("type"));
            var flags = ev == IntPtr.Zero ? 0 : ObjC.GetLong(ev, ObjC.Sel("modifierFlags"));
            // NSEventTypeRightMouseUp = 4; NSEventModifierFlagControl = 1 << 18.
            Clicked?.Invoke(type == 4 || (flags & (1 << 18)) != 0);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[astra] tray click: {e.Message}");
        }
    }

    private static void OnMenu(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            var tag = ObjC.GetLong(sender, ObjC.Sel("tag"));
            if (Entries.TryGetValue(tag, out var entry)) entry.OnSelect?.Invoke();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[astra] tray menu: {e.Message}");
        }
    }
}

/// <summary>An <c>NSMenu</c> built from the tray's menu model (Electron's <c>Menu.buildFromTemplate</c>, the parts the tray uses).</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacMenu : IDisposable
{
    private readonly List<long> _tags = [];

    private MacMenu(IntPtr handle) => Handle = handle;

    /// <summary>The <c>NSMenu</c> (owned).</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>Builds the menu; <paramref name="onSelect"/> runs a chosen item's action (on the main thread, inside the menu's tracking loop).</summary>
    public static MacMenu Build(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect)
    {
        var menu = new MacMenu(NewMenu());
        menu.Fill(menu.Handle, items, onSelect);
        return menu;
    }

    /// <summary>A flat menu of plain / checkbox items (bridge <c>showContextMenu</c>); <paramref name="onSelect"/> gets the item id.</summary>
    public static MacMenu Build(IReadOnlyList<Astra.Desktop.Core.Shell.ContextMenuItem> items, Action<string> onSelect)
    {
        var menu = new MacMenu(NewMenu());
        foreach (var item in items)
        {
            if (item.Separator)
            {
                ObjC.Send(menu.Handle, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem")));
                continue;
            }
            var id = item.Id;
            var entry = menu.Item(item.Label ?? "", id is null ? null : () => onSelect(id), item.Enabled, item.Checked);
            ObjC.Send(menu.Handle, ObjC.Sel("addItem:"), entry);
            ObjC.Release(entry);
        }
        return menu;
    }

    private static IntPtr NewMenu()
    {
        var menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:"), ObjC.Str(""));
        // Enabled states come from the model, not from responder-chain validation.
        ObjC.SendBool(menu, ObjC.Sel("setAutoenablesItems:"), 0);
        return menu;
    }

    private void Fill(IntPtr menu, IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect)
    {
        foreach (var item in items)
        {
            if (item.Kind == TrayMenuItemKind.Separator)
            {
                ObjC.Send(menu, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem")));
                continue;
            }
            var action = item.Action;
            var entry = Item(item.Label, action is null || item.Submenu is not null ? null : () => onSelect(action), item.Enabled, item.Checked, item.QuitAccelerator ? "q" : "");
            if (item.Sublabel is { } sub && ObjC.GetBool(entry, ObjC.Sel("respondsToSelector:"), ObjC.Sel("setSubtitle:")) != 0)
                ObjC.Send(entry, ObjC.Sel("setSubtitle:"), ObjC.Str(sub));
            if (item.Submenu is { } children)
            {
                var submenu = NewMenu();
                Fill(submenu, children, onSelect);
                ObjC.Send(entry, ObjC.Sel("setSubmenu:"), submenu);
                ObjC.Release(submenu);
            }
            ObjC.Send(menu, ObjC.Sel("addItem:"), entry);
            ObjC.Release(entry);
        }
    }

    private IntPtr Item(string title, Action? onSelect, bool enabled, bool on, string key = "")
    {
        var entry = ObjC.Send(ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
            ObjC.Str(title), onSelect is null ? IntPtr.Zero : ObjC.Sel("astraMenu:"), ObjC.Str(key));
        if (onSelect is not null)
        {
            var tag = MacTarget.Register(new MacMenuEntry(onSelect));
            _tags.Add(tag);
            ObjC.Send(entry, ObjC.Sel("setTarget:"), MacTarget.Instance);
            ObjC.SendLong(entry, ObjC.Sel("setTag:"), tag);
        }
        ObjC.SendBool(entry, ObjC.Sel("setEnabled:"), (byte)(enabled ? 1 : 0));
        if (on) ObjC.SendLong(entry, ObjC.Sel("setState:"), 1);
        return entry;
    }

    /// <summary>Pops the menu at the mouse (runs the menu's tracking loop and returns when it closed).</summary>
    public void PopUpAtMouse()
    {
        var p = ObjC.GetPoint(ObjC.Class("NSEvent"), ObjC.Sel("mouseLocation"));
        ObjC.PopUp(Handle, ObjC.Sel("popUpMenuPositioningItem:atLocation:inView:"), IntPtr.Zero, p, IntPtr.Zero);
    }

    public void Dispose()
    {
        MacTarget.Forget(_tags);
        _tags.Clear();
        ObjC.Release(Handle);
        Handle = IntPtr.Zero;
    }
}

/// <summary>
/// The macOS menu-bar item (Electron's <c>Tray</c> on darwin): a template image the system tints for light / dark menu bars, the configured
/// title beside it in monospaced digits, a tooltip, a click that opens the panel and a right / ⌃-click that opens the menu.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacStatusItem : IDisposable
{
    private IntPtr _item;
    private IntPtr _button;
    private MacMenu? _menu;

    public MacStatusItem(byte[] icon1x, byte[] icon2x, Action<bool> clicked)
    {
        MacTarget.Clicked = clicked;
        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        _item = ObjC.Send(ObjC.SendDouble(bar, ObjC.Sel("statusItemWithLength:"), -1), ObjC.Sel("retain"));
        _button = ObjC.Send(_item, ObjC.Sel("button"));

        var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), ObjC.Data(icon1x));
        var rep = ObjC.Send(ObjC.Class("NSBitmapImageRep"), ObjC.Sel("imageRepWithData:"), ObjC.Data(icon2x));
        ObjC.SendSize(rep, ObjC.Sel("setSize:"), new NSSize(16, 16));
        ObjC.Send(image, ObjC.Sel("addRepresentation:"), rep);
        ObjC.SendSize(image, ObjC.Sel("setSize:"), new NSSize(16, 16));
        ObjC.SendBool(image, ObjC.Sel("setTemplate:"), 1);
        ObjC.Send(_button, ObjC.Sel("setImage:"), image);
        ObjC.Release(image);
        // NSImageLeft: the title goes to the right of the icon.
        ObjC.SendLong(_button, ObjC.Sel("setImagePosition:"), 2);
        var font = ObjC.SendDouble2(ObjC.Class("NSFont"), ObjC.Sel("monospacedDigitSystemFontOfSize:weight:"), 0, 0);
        ObjC.Send(_button, ObjC.Sel("setFont:"), font);
        ObjC.Send(_button, ObjC.Sel("setTarget:"), MacTarget.Instance);
        ObjC.Send(_button, ObjC.Sel("setAction:"), ObjC.Sel("astraClick:"));
        // NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp: the action fires for both buttons.
        ObjC.SendLong(_button, ObjC.Sel("sendActionOn:"), (1 << 2) | (1 << 4));
        SetToolTip("Astra");
    }

    public void SetTitle(string title) => ObjC.Send(_button, ObjC.Sel("setTitle:"), ObjC.Str(title));

    public void SetToolTip(string text) => ObjC.Send(_button, ObjC.Sel("setToolTip:"), ObjC.Str(text));

    public void SetMenu(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect)
    {
        _menu?.Dispose();
        _menu = MacMenu.Build(items, onSelect);
    }

    /// <summary>Shows the menu under the item (Electron's <c>popUpContextMenu</c>): attached for one click, then detached so a normal click opens the panel again.</summary>
    public void PopUpMenu()
    {
        if (_menu is null) return;
        ObjC.Send(_item, ObjC.Sel("setMenu:"), _menu.Handle);
        ObjC.Send(_button, ObjC.Sel("performClick:"), IntPtr.Zero);
        ObjC.Send(_item, ObjC.Sel("setMenu:"), IntPtr.Zero);
    }

    /// <summary>The icon's screen rectangle in Avalonia coordinates; null when unknown.</summary>
    public ScreenRect? Bounds()
    {
        var window = ObjC.Send(_button, ObjC.Sel("window"));
        if (window == IntPtr.Zero) return null;
        var (x, y, w, h) = MacApp.FlipRect(ObjC.GetRect(window, ObjC.Sel("frame")));
        return new ScreenRect(x, y, w, h);
    }

    public void Dispose()
    {
        _menu?.Dispose();
        _menu = null;
        if (_item == IntPtr.Zero) return;
        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        ObjC.Send(bar, ObjC.Sel("removeStatusItem:"), _item);
        ObjC.Release(_item);
        _item = IntPtr.Zero;
    }
}
