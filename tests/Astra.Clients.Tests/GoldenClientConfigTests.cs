using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;

namespace Astra.Clients.Tests;

/// <summary>
/// Golden cases shared with the TypeScript port (<c>packages/client-config</c>, plan: enterprise-connect §3). A case is a
/// directory <c>tests/fixtures/client-config/&lt;client&gt;/&lt;case&gt;/</c> holding <c>case.json</c> (the steps to run) and
/// <c>home/</c> (the fake home before the first step). This test runs the real adapters and applier over it and compares
/// every plan, state and file with <c>expected/</c>; <c>ASTRA_UPDATE_FIXTURES=1</c> rewrites <c>expected/</c> instead.
/// The TS tests run the same cases, so a change here that alters an adapter's output fails them until it is ported.
/// The fake home path appears as <c>$HOME</c> in every file, plan and state value.
/// </summary>
public class GoldenClientConfigTests
{
    private const string HomeToken = "$HOME";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var root = CasesRoot();
        // ASTRA_FIXTURE_FILTER=<client>[/<case prefix>] limits a run (and a regeneration) to some cases.
        var filter = Environment.GetEnvironmentVariable("ASTRA_FIXTURE_FILTER");
        foreach (var file in Directory.GetFiles(root, "case.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
            if (string.IsNullOrEmpty(filter) || name.StartsWith(filter, StringComparison.Ordinal)) data.Add(name);
        }
        return data;
    }

    private static string CasesRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Astra.sln")))
                return Path.Combine(dir.FullName, "tests", "fixtures", "client-config");
        throw new DirectoryNotFoundException("Astra.sln not found above the test assembly.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Adapter_Output_Matches_The_Golden_Case(string name)
    {
        var caseDir = Path.Combine(CasesRoot(), name);
        var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(caseDir, "case.json")))!.AsObject();
        var update = Environment.GetEnvironmentVariable("ASTRA_UPDATE_FIXTURES") == "1";

        using var home = new TestHome();
        var store = new InMemoryClientConfigStateStore();
        var applier = new ClientConfigApplier(store, home.Env, home.BackupRoot);
        var registry = ClientAdapterRegistry.CreateDefault(home.Env, store);
        var adapter = registry.Require(spec["client"]!.GetValue<string>());

        foreach (var (key, value) in spec["env"]?.AsObject() ?? [])
            home.Variables[key] = Home(value!.GetValue<string>(), home.Path);
        CopyHome(Path.Combine(caseDir, "home"), home.Path);

        var expectedDir = Path.Combine(caseDir, "expected");
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var steps = spec["steps"]!.AsArray();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!.AsObject();
            var prefix = $"{i + 1:D2}-{step["op"]!.GetValue<string>()}";
            foreach (var edit in step["edit"]?.AsArray() ?? [])
            {
                var path = Path.Combine(home.Path, edit!["file"]!.GetValue<string>());
                if (edit["delete"]?.GetValue<bool>() == true) File.Delete(path);
                else home.WriteFile(path, Home(edit["write"]!.GetValue<string>(), home.Path));
            }

            switch (step["op"]!.GetValue<string>())
            {
                case "enable":
                    var plan = adapter.PlanEnable(Context(spec, step, home.Path));
                    actual[$"{prefix}/plan.json"] = Serialize(PlanJson(plan, home.Path));
                    applier.Apply(plan);
                    break;
                case "disable":
                    actual[$"{prefix}/report.json"] = Serialize(ReportJson(applier.Disable(adapter.PlanDisable()), home.Path));
                    break;
                case "force-restore":
                    actual[$"{prefix}/report.json"] = Serialize(ReportJson(applier.ForceRestore(adapter.PlanDisable()), home.Path));
                    break;
                case "purge":
                    actual[$"{prefix}/report.json"] = Serialize(ReportJson(applier.Disable(adapter.PlanPurge()), home.Path));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step '{step["op"]}' in {name}.");
            }
            actual[$"{prefix}/state.json"] = Serialize(StateJson(store, adapter.Kind, home.Path));
            foreach (var (relative, text) in Snapshot(home.Path))
                actual[$"{prefix}/files/{relative}"] = text;
        }

        if (update)
        {
            if (Directory.Exists(expectedDir)) Directory.Delete(expectedDir, recursive: true);
            foreach (var (relative, text) in actual)
            {
                var target = Path.Combine(expectedDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text, new UTF8Encoding(false));
            }
            return;
        }

        var expected = Directory.Exists(expectedDir)
            ? Directory.GetFiles(expectedDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(expectedDir, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal)
            : [];
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys);
        foreach (var (relative, text) in actual)
            Assert.True(string.Equals(expected[relative], text, StringComparison.Ordinal),
                $"{name}: {relative} differs from the golden file (run with ASTRA_UPDATE_FIXTURES=1 to regenerate).\n--- expected\n{expected[relative]}\n--- actual\n{text}");
    }

    private static EnableContext Context(JsonObject spec, JsonObject step, string home)
    {
        // A step's own "context" members replace the case's.
        JsonNode? Pick(string key) => step["context"]?[key] ?? spec[key];
        return new EnableContext
        {
            GatewayBaseUrl = Pick("gatewayBaseUrl")!.GetValue<string>(),
            LocalKey = Pick("localKey")!.GetValue<string>(),
            Model = Pick("model")?.GetValue<string>(),
            Extras = Pick("extras")?.DeepClone().AsObject(),
            ModelsWithoutContext1M = Pick("modelsWithoutContext1M")?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(),
        };
    }

    private static string Home(string text, string home) => text.Replace(HomeToken, home, StringComparison.Ordinal);

    private static string Normalize(string text, string home) => text.Replace(home, HomeToken, StringComparison.Ordinal);

    private static string Serialize(JsonNode node) => node.ToJsonString(Pretty) + "\n";

    private static JsonArray PlanJson(ConfigChangePlan plan, string home) => new(plan.Changes.Select(c => (JsonNode)new JsonObject
    {
        ["file"] = Normalize(c.File, home),
        ["format"] = c.Format.ToString().ToLowerInvariant(),
        ["keyPath"] = c.KeyPath,
        ["kind"] = c.Kind.ToString().ToLowerInvariant(),
        ["before"] = c.Before is null ? null : Normalize(c.Before, home),
        ["after"] = c.After is null ? null : Normalize(c.After, home),
    }).ToArray());

    private static JsonArray StateJson(InMemoryClientConfigStateStore store, string kind, string home) =>
        new(store.List(kind).Select(e => (JsonNode)new JsonObject
        {
            ["file"] = Normalize(e.FilePath, home),
            ["keyPath"] = e.KeyPath,
            ["originalAbsent"] = e.OriginalAbsent,
            ["original"] = e.OriginalValueJson is null ? null : Normalize(e.OriginalValueJson, home),
            ["applied"] = e.AppliedValueJson is null ? null : Normalize(e.AppliedValueJson, home),
        }).ToArray());

    private static JsonObject ReportJson(RestoreReport report, string home) => new()
    {
        ["keys"] = new JsonArray(report.Keys.Select(k => (JsonNode)new JsonObject
        {
            ["file"] = Normalize(k.FilePath, home),
            ["keyPath"] = k.KeyPath,
            ["outcome"] = k.Outcome.ToString().ToLowerInvariant(),
            ["detail"] = k.Detail,
        }).ToArray()),
        ["deletedFiles"] = new JsonArray(report.DeletedFiles.Select(f => (JsonNode)Normalize(f, home)).ToArray()),
        ["warnings"] = new JsonArray(report.Warnings.Select(w => (JsonNode)Normalize(w, home)).ToArray()),
    };

    /// <summary>Every file under the fake home except Astra's own backups, as relative path → text.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Snapshot(string home)
    {
        foreach (var file in Directory.GetFiles(home, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(home, file).Replace('\\', '/');
            if (relative.StartsWith(".astra/", StringComparison.Ordinal)) continue;
            // A BOM is part of the bytes a restore must reproduce: keep it visible in the text.
            yield return new(relative, Normalize(Encoding.UTF8.GetString(File.ReadAllBytes(file)), home));
        }
    }

    private static void CopyHome(string source, string home)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(home, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Home(Encoding.UTF8.GetString(File.ReadAllBytes(file)), home), new UTF8Encoding(false));
        }
    }
}
