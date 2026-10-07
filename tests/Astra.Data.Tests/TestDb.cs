using Astra.Core;

namespace Astra.Data.Tests;

/// <summary>Fresh temp-directory database per test.</summary>
internal static class TestDb
{
    public static AstraPaths NewPaths(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "astra-data-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new AstraPaths(dir);
    }

    /// <summary>Migrates a fresh database (no seed import by default).</summary>
    public static Task<AstraDatabase> InitializeAsync(bool importSeed = false) =>
        AstraDatabase.InitializeAsync(NewPaths(out _), importSeed);
}
