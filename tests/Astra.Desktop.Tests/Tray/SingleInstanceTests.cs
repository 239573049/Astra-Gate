using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Tests.Tray;

/// <summary>The single-instance lock and the second-launch signal (Electron's requestSingleInstanceLock + 'second-instance'), on scratch data dirs.</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-si-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_second_claim_fails_while_the_first_holds_and_succeeds_after_release()
    {
        var first = SingleInstance.TryClaim(_root);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryClaim(_root));
        first!.Dispose();
        using var again = SingleInstance.TryClaim(_root);
        Assert.NotNull(again);
    }

    [Fact]
    public void A_second_launch_activates_the_running_instance()
    {
        using var first = SingleInstance.TryClaim(_root)!;
        using var activated = new ManualResetEventSlim();
        first.Listen(activated.Set);

        Assert.True(SingleInstance.SignalRunning(_root, TimeSpan.FromSeconds(5)));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        // It keeps answering.
        activated.Reset();
        Assert.True(SingleInstance.SignalRunning(_root, TimeSpan.FromSeconds(5)));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Signalling_with_nobody_listening_gives_up_after_the_timeout()
    {
        Assert.False(SingleInstance.SignalRunning(_root, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void Each_data_directory_has_its_own_pipe()
    {
        Assert.NotEqual(SingleInstance.PipeNameFor(_root), SingleInstance.PipeNameFor(_root + "-other"));
        Assert.Equal(SingleInstance.PipeNameFor(_root), SingleInstance.PipeNameFor(_root + Path.DirectorySeparatorChar));
        Assert.True(SingleInstance.PipeNameFor(_root).Length < 40);
    }
}
