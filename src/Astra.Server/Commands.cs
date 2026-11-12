using Astra.Clients.Adapters;
using Astra.Clients.Config;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
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
        // Clients plugins added are restored from the descriptor their row keeps: no plugin code runs offline.
        var remembered = (await db.Clients.ListAsync())
            .Where(r => ClientKinds.IsPluginKind(r.Kind))
            .Select(r => Json.Deserialize<JsonObject>(r.ExtraJson)?[Api.ClientService.PluginExtrasKey]?.ToJsonString())
            .Select(j => j is null ? null : Json.Deserialize<AgentPluginDescriptor>(j))
            .OfType<AgentPluginDescriptor>()
            .ToList();
        var registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState, remembered: remembered);
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
        // The MCP gateway entry of Claude Code and Codex (issue #34) has its own state, independent of the take-over above.
        try
        {
            foreach (var (kind, report) in new ClientMcpApplier(env, db.ClientConfigState, applier).RemoveAll())
            {
                var drifted = report.Keys.Where(k => k.Outcome == RestoreKeyOutcome.Drifted).ToList();
                if (report.Keys.Any(k => k.Outcome == RestoreKeyOutcome.Restored)) Console.WriteLine($"{kind}: removed the MCP gateway entry");
                foreach (var key in drifted) Console.Error.WriteLine($"  Left modified key untouched: {key.FilePath}:{key.KeyPath}");
                incomplete |= drifted.Count > 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Astra.Clients.Editing.EditorException)
        {
            Console.Error.WriteLine($"MCP gateway entries: restore failed: {ex.Message}");
            incomplete = true;
        }
        // Local (stdio) MCP servers Astra wrote into Claude Code and Codex (issue #34).
        try
        {
            foreach (var (kind, report) in new ClientMcpServerApplier(env, db.ClientConfigState, applier).RemoveAll())
            {
                var drifted = report.Keys.Where(k => k.Outcome == RestoreKeyOutcome.Drifted).ToList();
                foreach (var key in drifted) Console.Error.WriteLine($"  Left modified key untouched: {key.FilePath}:{key.KeyPath}");
                incomplete |= drifted.Count > 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Astra.Clients.Editing.EditorException)
        {
            Console.Error.WriteLine($"Local MCP entries: restore failed: {ex.Message}");
            incomplete = true;
        }
        var skillsIncomplete = false;
        // Skills are the user's content, not gateway settings: they are given back (links become real directories) whatever --purge says.
        try
        {
            var library = new Astra.Clients.Library.SkillLibrary(env, paths, db.Library);
            foreach (var outcome in await library.ReturnAsync(null))
            {
                if (outcome.Ok) continue;
                Console.Error.WriteLine($"Skill {outcome.Name}: could not be given back: {outcome.Reason}");
                skillsIncomplete = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Astra.Clients.Library.SkillLibraryException)
        {
            Console.Error.WriteLine($"Skills: give-back failed: {ex.Message}");
            skillsIncomplete = true;
        }
        // 3 = a Skill is still a link into ~/.astra/library: `astra uninstall --purge` must not delete that directory.
        return skillsIncomplete ? 3 : incomplete ? 1 : 0;
    }
}
