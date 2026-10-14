using Astra.Clients.Config;

namespace Astra.Clients.Tests;

/// <summary>A throwaway fake home directory + environment, so real user config is never touched.</summary>
public sealed class TestHome : IDisposable
{
    public TestHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astra-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        Env = new ClientEnvironment(Path, "osx", name => Variables.TryGetValue(name, out var value) ? value : null);
    }

    /// <summary>Absolute path of the fake home directory.</summary>
    public string Path { get; }

    public ClientEnvironment Env { get; }

    /// <summary>Environment variables visible through <see cref="ClientEnvironment.GetEnvironmentVariable"/>.</summary>
    public Dictionary<string, string?> Variables { get; }

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void WriteFile(string path, string content) => WriteFileBytes(path, System.Text.Encoding.UTF8.GetBytes(content));

    public void WriteFileBytes(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, bytes);
    }

    public string ReadFile(string path) => System.IO.File.ReadAllText(path);

    public byte[] ReadFileBytes(string path) => System.IO.File.ReadAllBytes(path);

    public bool FileExists(string path) => System.IO.File.Exists(path);

    public string BackupRoot => System.IO.Path.Combine(Path, ".astra", "backups", "client-configs");

    /// <summary>Fixture content copied from the test assembly's Fixtures directory.</summary>
    public static string Fixture(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        return System.IO.File.ReadAllText(System.IO.Path.Combine(baseDir, "Fixtures", name));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Shared gateway parameters for the tests.</summary>
public static class TestGateway
{
    public const string BaseUrl = "http://127.0.0.1:17321";

    public static EnableContext Context(string localKey, string? model = null, System.Text.Json.Nodes.JsonObject? extras = null) => new()
    {
        GatewayBaseUrl = BaseUrl,
        LocalKey = localKey,
        Model = model,
        Extras = extras,
    };

    /// <summary>
    /// Sets one model's "protocols" hint in a "models" extra (the protocols the gateway passes it through in, preferred
    /// first — what ClientService derives from the provider) and returns the extra.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject Native(System.Text.Json.Nodes.JsonObject extras, string id, params string[] protocols)
    {
        var model = (System.Text.Json.Nodes.JsonObject)extras["models"]![id]!;
        model["protocols"] = new System.Text.Json.Nodes.JsonArray([.. protocols.Select(p => (System.Text.Json.Nodes.JsonNode)p)]);
        return extras;
    }

    /// <summary>Sets the user's per-model protocol choice ("protocolOverrides") in an extras object and returns it.</summary>
    public static System.Text.Json.Nodes.JsonObject Override(System.Text.Json.Nodes.JsonObject extras, string id, string protocol)
    {
        if (extras["protocolOverrides"] is not System.Text.Json.Nodes.JsonObject overrides)
            extras["protocolOverrides"] = overrides = new System.Text.Json.Nodes.JsonObject();
        overrides[id] = protocol;
        return extras;
    }
}
