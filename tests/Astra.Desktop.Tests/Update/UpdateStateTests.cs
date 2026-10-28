using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("astra-upd-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class UpdateStateTests
{
    private static readonly Func<DateTimeOffset> Clock = () => DateTimeOffset.Parse("2026-10-26T10:00:00.123Z");

    [Fact]
    public void Round_trips_and_tolerates_missing_or_corrupt_files()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "sub", "update-state.json");
        Assert.Null(UpdateState.Read(file));

        UpdateState.Write(file, UpdateState.Initial(Clock) with { Phase = UpdatePhase.RollingBack, TargetVersion = "0.2.0", FailedCount = 1, Error = "boom" }, Clock);
        var read = UpdateState.Read(file);
        Assert.Equal(new UpdateState(UpdatePhase.RollingBack, 1, "2026-10-26T10:00:00.123Z", "0.2.0", null, null, "boom"), read);

        File.WriteAllText(file, "{not json");
        Assert.Null(UpdateState.Read(file));
        File.WriteAllText(file, """{"phase":"bogus"}""");
        Assert.Null(UpdateState.Read(file));
        File.WriteAllText(file, "  ");
        Assert.Null(UpdateState.Read(file));
        Assert.Equal(2, UpdateState.MaxConsecutiveFailures);
    }

    [Fact]
    public void Written_bytes_match_what_the_TS_executor_writes()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "s.json");
        UpdateState.Write(file, new UpdateState(UpdatePhase.Downloading, 1, "x", "0.2.0", "0.1.0", "downloading"), Clock);
        Assert.Equal("{\n  \"phase\": \"downloading\",\n  \"targetVersion\": \"0.2.0\",\n  \"previousVersion\": \"0.1.0\",\n  \"step\": \"downloading\",\n  \"failedCount\": 1,\n  \"updatedAt\": \"2026-10-26T10:00:00.123Z\"\n}\n",
            File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Reads_a_file_the_TS_executor_wrote()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "s.json");
        File.WriteAllText(file, """{"phase":"failed","targetVersion":"0.2.0","error":null,"failedCount":2,"updatedAt":"2026-01-01T00:00:00.000Z","unknown":true}""");
        var s = UpdateState.Read(file)!;
        Assert.Equal((UpdatePhase.Failed, 2, null), (s.Phase, s.FailedCount, s.Error));
        File.WriteAllText(file, """{"phase":"idle"}""");
        Assert.Equal(0, UpdateState.Read(file)!.FailedCount);
    }
}
