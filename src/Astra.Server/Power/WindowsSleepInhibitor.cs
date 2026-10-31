using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Astra.Server.Power;

/// <summary>
/// Windows: a <c>PowerRequestSystemRequired</c> power request (<c>PowerCreateRequest</c> / <c>PowerSetRequest</c>). Unlike
/// <c>SetThreadExecutionState</c> it belongs to a handle, not to the calling thread, so it holds whichever pool thread takes
/// or releases it. It keeps the system from idle sleep only (the display still turns off; the lid and the power button
/// still sleep), shows in <c>powercfg /requests</c> under the reason given, and goes away with the handle when the process exits.
/// Plain blittable <c>DllImport</c>s (no unsafe code, Native AOT safe), like Autostart/WindowsConsole.cs.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSleepInhibitor(ILogger log) : ISleepInhibitor
{
    private const uint ContextVersion = 0; // POWER_REQUEST_CONTEXT_VERSION
    private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
    private const int SystemRequired = 1; // PowerRequestSystemRequired

    private IntPtr _request, _reason;
    private bool _warned;

    public bool Acquire(string reason)
    {
        if (_request != IntPtr.Zero) return true;
        // Kept until the request is closed: the context points at it.
        var text = Marshal.StringToHGlobalUni(reason);
        var context = new ReasonContext { Version = ContextVersion, Flags = SimpleString, SimpleReasonString = text };
        var request = PowerCreateRequest(ref context);
        if (request == IntPtr.Zero || request == new IntPtr(-1))
        {
            Warn("PowerCreateRequest", Marshal.GetLastPInvokeError());
            Marshal.FreeHGlobal(text);
            return false;
        }
        if (PowerSetRequest(request, SystemRequired) == 0)
        {
            Warn("PowerSetRequest", Marshal.GetLastPInvokeError());
            CloseHandle(request);
            Marshal.FreeHGlobal(text);
            return false;
        }
        (_request, _reason) = (request, text);
        return true;
    }

    public void Release()
    {
        if (_request == IntPtr.Zero) return;
        if (PowerClearRequest(_request, SystemRequired) == 0)
            log.LogDebug("PowerClearRequest failed with error {Error}", Marshal.GetLastPInvokeError());
        CloseHandle(_request);
        Marshal.FreeHGlobal(_reason);
        (_request, _reason) = (IntPtr.Zero, IntPtr.Zero);
    }

    private void Warn(string call, int error)
    {
        if (_warned) return;
        _warned = true;
        log.LogWarning("Could not keep the system awake: {Call} failed with error {Error}", call, error);
    }

    /// <summary>
    /// <c>REASON_CONTEXT</c>: the union is laid out as its larger member (<c>Detailed</c>: module, id, count, strings), whose
    /// first field <see cref="SimpleReasonString"/> overlays — so the struct has the native size on 32- and 64-bit alike.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);
}
