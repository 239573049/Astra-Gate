namespace Astra.Server.Power;

/// <summary>
/// An operating system's idle-sleep inhibitor (<see cref="KeepAwakeService"/>). Only idle sleep: the display may still turn
/// off, and closing the lid or choosing sleep still sleeps. Calls come one at a time, from <see cref="KeepAwakeService"/>.
/// </summary>
public interface ISleepInhibitor
{
    /// <summary>
    /// Takes the inhibitor (a no-op while it is held). False when this machine has none to give; the implementation logs
    /// why, once.
    /// </summary>
    bool Acquire(string reason);

    /// <summary>Lets go of the inhibitor; a no-op when it is not held.</summary>
    void Release();
}

/// <summary>The inhibitor for the running OS.</summary>
public static class SleepInhibitors
{
    /// <summary>
    /// macOS: an IOKit power assertion (what <c>caffeinate -i</c> takes); Windows: a system-required power request;
    /// Linux: <c>systemd-inhibit</c>; anything else: nothing.
    /// </summary>
    public static ISleepInhibitor ForThisMachine(ILogger log)
    {
        if (OperatingSystem.IsMacOS()) return new MacSleepInhibitor(log);
        if (OperatingSystem.IsWindows()) return new WindowsSleepInhibitor(log);
        if (OperatingSystem.IsLinux()) return new SystemdSleepInhibitor(SystemdSleepInhibitor.Find(), log);
        return new NoSleepInhibitor(log);
    }
}

/// <summary>An OS Astra has no inhibitor for: logs that once and never holds anything.</summary>
internal sealed class NoSleepInhibitor(ILogger log) : ISleepInhibitor
{
    private bool _logged;

    public bool Acquire(string reason)
    {
        if (!_logged)
        {
            _logged = true;
            log.LogInformation("Sleep prevention is not available on this operating system; the setting has no effect");
        }
        return false;
    }

    public void Release()
    {
    }
}
