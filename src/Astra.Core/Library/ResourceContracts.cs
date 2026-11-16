namespace Astra.Core.Library;

/// <summary>The kinds of resource the library keeps besides Skills (<c>library_items.kind</c>).</summary>
public static class ResourceKinds
{
    /// <summary>A text placed into a client's instruction file as an Astra-owned marked section.</summary>
    public const string Instruction = "instruction";

    /// <summary>A sub-agent definition, written as the client's own agent file.</summary>
    public const string Agent = "agent";

    /// <summary>A slash command, written as the client's own command file.</summary>
    public const string Command = "command";

    /// <summary>A command a client runs at a point of its turn.</summary>
    public const string Hook = "hook";

    /// <summary>An environment variable a client sets for what it runs.</summary>
    public const string Env = "env";

    public static readonly IReadOnlyList<string> All = [Instruction, Agent, Command, Hook, Env];
}

/// <summary>How an item was written (<c>library_writes.mode</c>).</summary>
public static class WriteModes
{
    /// <summary>A whole file Astra created (a sub-agent, a command).</summary>
    public const string File = "file";

    /// <summary>A marked section inside a file that is mostly the user's.</summary>
    public const string Block = "block";

    /// <summary>One entry in a client's hook array.</summary>
    public const string Hook = "hook";

    /// <summary>A key in a client's settings (an environment variable).</summary>
    public const string Config = "config";

    /// <summary>The link of a client's skill router.</summary>
    public const string Router = "router";
}

/// <summary>Where an item goes: a client, user-wide (<see cref="Project"/> empty) or into a project directory.</summary>
public sealed record LibraryTarget(string Client, string Project = "");

/// <summary>An item of the library (row of <c>library_items</c>).</summary>
public sealed class LibraryItemRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ResourceKinds.Instruction;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The instruction text, the agent's prompt, the command's prompt, the hook's command or the variable's value.</summary>
    public string Body { get; set; } = "";

    /// <summary>Kind-specific settings as JSON (a hook's event, matcher and timeout; an agent's model).</summary>
    public string ExtraJson { get; set; } = "{}";

    /// <summary>A JSON array of <see cref="LibraryTarget"/>.</summary>
    public string TargetsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Something Astra wrote for an item (row of <c>library_writes</c>): the ledger of what Astra may remove.</summary>
public sealed class LibraryWriteRecord
{
    public string Id { get; set; } = "";
    public string? ItemId { get; set; }
    public string Client { get; set; } = "";
    public string Project { get; set; } = "";
    public string Path { get; set; } = "";
    public string Mode { get; set; } = WriteModes.File;
    public string Locator { get; set; } = "";
    public string ContentHash { get; set; } = "";

    /// <summary>A hook's exact JSON entry, so an entry the user changed is theirs from then on.</summary>
    public string? Entry { get; set; }

    public DateTimeOffset WrittenAt { get; set; }
}

/// <summary>A project directory things can be placed into (row of <c>library_projects</c>).</summary>
public sealed class LibraryProjectRecord
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>The ledger of the non-Skill library, so the engine needs no reference to the database assembly.</summary>
public interface ILibraryResourceLedger
{
    Task<IReadOnlyList<LibraryItemRecord>> ListItemsAsync(string? kind = null, CancellationToken ct = default);
    Task<LibraryItemRecord?> GetItemAsync(string id, CancellationToken ct = default);
    Task<LibraryItemRecord?> GetItemByNameAsync(string kind, string name, CancellationToken ct = default);
    Task UpsertItemAsync(LibraryItemRecord item, CancellationToken ct = default);
    Task DeleteItemAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<LibraryWriteRecord>> ListWritesAsync(string? itemId = null, CancellationToken ct = default);
    Task UpsertWriteAsync(LibraryWriteRecord write, CancellationToken ct = default);
    Task DeleteWriteAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<LibraryProjectRecord>> ListProjectsAsync(CancellationToken ct = default);
    Task UpsertProjectAsync(LibraryProjectRecord project, CancellationToken ct = default);
    Task DeleteProjectAsync(string path, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListRouterClientsAsync(CancellationToken ct = default);
    Task SetRouterClientAsync(string client, bool on, CancellationToken ct = default);
}
