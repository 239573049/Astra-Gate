using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;

namespace Astra.Clients.Config;

/// <summary>
/// Uniform editor interface over the four formats, shared by the applier (execution) and the
/// adapters (plan simulation and inspection).
/// </summary>
internal abstract class ConfigEditorOps
{
    public abstract string Text { get; }

    /// <summary>Current value source text (null = absent; "true"/null for tables — TOML tables, JSON containers owned whole).</summary>
    public abstract string? Read(ConfigChange change);

    public abstract void Apply(ConfigChange change, List<string> warnings);

    /// <summary>Restores a raw source text (byte-exact where the format allows).</summary>
    public abstract void RestoreSource(ConfigChange change, string source);

    public abstract void Remove(ConfigChange change);

    public static ConfigEditorOps Create(ConfigFileFormat format, string text) => format switch
    {
        ConfigFileFormat.Toml => new TomlOps(new TomlEditor(text)),
        ConfigFileFormat.Json => new JsonOps(new JsoncEditor(text)),
        ConfigFileFormat.Env => new EnvOps(new DotEnvEditor(text)),
        ConfigFileFormat.Yaml => new YamlOps(new YamlEditor(text)),
        ConfigFileFormat.ShellBlock => new ShellBlockOps(new ShellBlockEditor(text)),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private sealed class ShellBlockOps : ConfigEditorOps
    {
        private ShellBlockEditor _editor;

        public ShellBlockOps(ShellBlockEditor editor) => _editor = editor;

        public override string Text => _editor.Text;

        // The key path is the block id; the value is the block body (raw text, like .env values).
        public override string? Read(ConfigChange change) => _editor.Get(change.KeyPath);

        public override void Apply(ConfigChange change, List<string> warnings)
        {
            _ = warnings;
            if (change.After is null) { Remove(change); return; }
            _editor = _editor.Set(change.KeyPath, ConfigValueCodec.DecodeToSource(ConfigFileFormat.ShellBlock, change.After)!);
        }

        public override void RestoreSource(ConfigChange change, string source) => _editor = _editor.Set(change.KeyPath, source);

        public override void Remove(ConfigChange change) => _editor = _editor.Remove(change.KeyPath);
    }

    private sealed class TomlOps : ConfigEditorOps
    {
        // The editors are immutable: every mutation returns the updated instance, so keep the field
        // mutable and always reassign.
        private TomlEditor _editor;

        public TomlOps(TomlEditor editor) => _editor = editor;

        public override string Text => _editor.Text;

        public override string? Read(ConfigChange change) => change.Kind == ConfigChangeKind.Table
            ? _editor.TableExists(change.KeyPath) ? "true" : null
            : _editor.GetValue(change.KeyPath);

        public override void Apply(ConfigChange change, List<string> warnings)
        {
            _ = warnings;
            if (change.After is null) { Remove(change); return; }
            if (change.Kind == ConfigChangeKind.Table)
            {
                // The "after" of a table change is a JSON object mapping item keys to raw TOML value texts.
                var items = JsonSerializer.Deserialize(change.After, JsonContexts.Info<JsonObject>(Json.Storage)) ?? new JsonObject();
                _editor = _editor.InsertTable(change.KeyPath, items
                    .Select(p => new KeyValuePair<string, string>(p.Key, (p.Value as JsonValue)?.GetValue<string>()
                        ?? throw new EditorException($"Table change for '{change.KeyPath}' has a non-string item value for '{p.Key}'.")))
                    .ToList());
            }
            else
            {
                _editor = _editor.Set(change.KeyPath, ConfigValueCodec.DecodeToSource(ConfigFileFormat.Toml, change.After)!);
            }
        }

        public override void RestoreSource(ConfigChange change, string source) => _editor = _editor.Set(change.KeyPath, source);

        public override void Remove(ConfigChange change)
        {
            _editor = change.Kind == ConfigChangeKind.Table ? _editor.RemoveTable(change.KeyPath) : _editor.Remove(change.KeyPath);
        }
    }

    private sealed class JsonOps : ConfigEditorOps
    {
        private JsoncEditor _editor;

        public JsonOps(JsoncEditor editor) => _editor = editor;

        public override string Text => _editor.Text;

        // A "table" in JSON is a container Astra owns as a whole (e.g. an array element it created): like a TOML
        // table it reads as "true"/null, so keys the client adds inside it later never count as drift.
        public override string? Read(ConfigChange change) => change.Kind == ConfigChangeKind.Table
            ? _editor.Has(change.KeyPath) ? "true" : null
            : _editor.Get(change.KeyPath)?.ToJsonString();

        public override void Apply(ConfigChange change, List<string> warnings)
        {
            _ = warnings;
            if (change.After is null) { Remove(change); return; }
            _editor = _editor.SetRaw(change.KeyPath, change.After);
        }

        public override void RestoreSource(ConfigChange change, string source) => _editor = _editor.SetRaw(change.KeyPath, source);

        public override void Remove(ConfigChange change) => _editor = _editor.Remove(change.KeyPath);
    }

    private sealed class EnvOps : ConfigEditorOps
    {
        private DotEnvEditor _editor;

        public EnvOps(DotEnvEditor editor) => _editor = editor;

        public override string Text => _editor.Text;

        public override string? Read(ConfigChange change) => _editor.GetRaw(change.KeyPath);

        public override void Apply(ConfigChange change, List<string> warnings)
        {
            _ = warnings;
            if (change.After is null) { Remove(change); return; }
            _editor = _editor.SetRaw(change.KeyPath, ConfigValueCodec.DecodeToSource(ConfigFileFormat.Env, change.After)!);
        }

        public override void RestoreSource(ConfigChange change, string source) => _editor = _editor.SetRaw(change.KeyPath, source);

        public override void Remove(ConfigChange change) => _editor = _editor.Remove(change.KeyPath);
    }

    private sealed class YamlOps : ConfigEditorOps
    {
        private YamlEditor _editor;

        public YamlOps(YamlEditor editor) => _editor = editor;

        public override string Text => _editor.Text;

        // A "table" in YAML is a whole row of a sequence document (a selector path) that Astra owns: "true"/null, like JSON.
        public override string? Read(ConfigChange change) => change.Kind == ConfigChangeKind.Table
            ? _editor.Has(change.KeyPath) ? "true" : null
            : _editor.GetJsonText(change.KeyPath);

        public override void Apply(ConfigChange change, List<string> warnings)
        {
            _ = warnings;
            if (change.After is null) { Remove(change); return; }
            _editor = _editor.SetRaw(change.KeyPath, change.After);
        }

        // YAML values are restored from their JSON data model; ClientConfigApplier then puts the original
        // bytes back once the document is semantically what it was before.
        public override void RestoreSource(ConfigChange change, string source) => _editor = _editor.SetRaw(change.KeyPath, source);

        public override void Remove(ConfigChange change) => _editor = _editor.Remove(change.KeyPath);
    }
}
