using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Data;

namespace Astra.Server;

/// <summary>Offline subcommands that must work without the HTTP server.</summary>
public static class Commands
{
    public static async Task<int> MigrateAsync(AstraPaths paths)
    {
        await AstraDatabase.InitializeAsync(paths);
        Console.WriteLine("Database is up to date.");
        return 0;
    }

    public static async Task<int> RestoreAllAsync(AstraPaths paths, bool purge, ClientEnvironment? environment = null)
    {
        var db = await AstraDatabase.InitializeAsync(paths, importSeed: false);
        var env = environment ?? ClientEnvironment.Real;
        var registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState);
        var applier = new ClientConfigApplier(db.ClientConfigState, env, paths.ClientBackupsDir);
        var incomplete = false;
        foreach (var adapter in registry.All)
        {
            try
            {
                var report = applier.Disable(purge ? adapter.PlanPurge() : adapter.PlanDisable());
                if (await db.Clients.GetAsync(adapter.Kind) is { } record)
                {
                    record.Enabled = false;
                    await db.Clients.UpsertAsync(record);
                }
                var restored = report.Keys.Count(k => k.Outcome == RestoreKeyOutcome.Restored);
                var drifted = report.Keys.Where(k => k.Outcome == RestoreKeyOutcome.Drifted).ToList();
                Console.WriteLine($"{adapter.Kind}: restored {restored}, drifted {drifted.Count}, removed {report.DeletedFiles.Count} files");
                foreach (var key in drifted) Console.Error.WriteLine($"  Left modified key untouched: {key.FilePath}:{key.KeyPath}");
                incomplete |= drifted.Count > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Astra.Clients.Editing.EditorException)
            {
                Console.Error.WriteLine($"{adapter.Kind}: restore failed: {ex.Message}");
                incomplete = true;
            }
        }
        return incomplete ? 1 : 0;
    }
}
