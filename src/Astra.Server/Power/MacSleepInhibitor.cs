using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Astra.Server.Power;

/// <summary>
/// macOS: an IOKit power assertion of type <c>PreventUserIdleSystemSleep</c> — what <c>caffeinate -i</c> takes. It keeps
/// the system from idle sleep only (the display still sleeps, closing the lid still sleeps), shows in
/// <c>pmset -g assertions</c> under the reason given, and powerd drops it when the process exits, however it exits.
/// Plain blittable <c>DllImport</c>s (no unsafe code, Native AOT safe), like Astra.Desktop's MacInterop.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSleepInhibitor(ILogger log) : ISleepInhibitor
{
    /// <summary><c>kIOPMAssertionTypePreventUserIdleSystemSleep</c>.</summary>
    internal const string AssertionType = "PreventUserIdleSystemSleep";

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const uint LevelOn = 255; // kIOPMAssertionLevelOn

    private uint _id;
    private bool _held, _warned;

    public bool Acquire(string reason)
    {
        if (_held) return true;
        var type = CFStringCreateWithCString(IntPtr.Zero, AssertionType, Utf8);
        var name = CFStringCreateWithCString(IntPtr.Zero, reason, Utf8);
        try
        {
            var result = type == IntPtr.Zero || name == IntPtr.Zero ? -1 : IOPMAssertionCreateWithName(type, LevelOn, name, out _id);
            if (result == 0) return _held = true;
            if (!_warned)
            {
                _warned = true;
                log.LogWarning("Could not keep the system awake: IOPMAssertionCreateWithName returned 0x{Result:x8}", result);
            }
            return false;
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    public void Release()
    {
        if (!_held) return;
        _held = false;
        var result = IOPMAssertionRelease(_id);
        if (result != 0) log.LogDebug("IOPMAssertionRelease returned 0x{Result:x8}", result);
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint id);
}
