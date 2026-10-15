using Mc.Scripting;
using Mc.Scripting.Permissions;
using System.Text.Json.Serialization;

namespace Charter.Model;

// The store document (plugins/charter/data/store.json). One human-editable JSON document; the file is canonical.
// Contract shared by every part of the plugin: change it only through the owner of Core (see DESIGN.md).

/// <summary>The whole permission dataset.</summary>
public sealed class StoreDoc
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>Server id used in the context tuple (0 = standalone). Matches ramjet.toml permissions.server_id.</summary>
    public int ServerId { get; set; }

    /// <summary>Groups by lower-case name. "default" always exists (every player is in it, evaluated last).</summary>
    public Dictionary<string, GroupDoc> Groups { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Users by uuid text (lower-case, dashed).</summary>
    public Dictionary<string, UserDoc> Users { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Tracks by lower-case name: ordered group names for promote/demote.</summary>
    public Dictionary<string, TrackDoc> Tracks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Plugin context slots (at most 2). Index in the list = slot number.</summary>
    public List<ContextSlotDoc> Slots { get; set; } = [];

    /// <summary>Unknown keys kept on round trip (import reports them).</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class GroupDoc
{
    /// <summary>Higher weight = earlier layer (wins over lower weights).</summary>
    public int Weight { get; set; }

    /// <summary>Parent group names (inherited; evaluated after this group).</summary>
    public List<Membership> Parents { get; set; } = [];

    public List<RuleDoc> Rules { get; set; } = [];

    /// <summary>Typed meta: key -> value (string form; the reader parses it per MetaKey type).</summary>
    public List<MetaDoc> Meta { get; set; } = [];

    public List<AffixDoc> Prefixes { get; set; } = [];
    public List<AffixDoc> Suffixes { get; set; } = [];

    /// <summary>Optional display name for chat/tab.</summary>
    public string? DisplayName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class UserDoc
{
    /// <summary>Last known name (informational; lookups by name use it).</summary>
    public string? Name { get; set; }

    /// <summary>Groups the user is in (besides the implicit "default").</summary>
    public List<Membership> Groups { get; set; } = [];

    public List<RuleDoc> Rules { get; set; } = [];
    public List<MetaDoc> Meta { get; set; } = [];
    public List<AffixDoc> Prefixes { get; set; } = [];
    public List<AffixDoc> Suffixes { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>Membership in a group (user -> group, or group -> parent), optionally temporary and context-bound.</summary>
public sealed class Membership
{
    public string Group { get; set; } = "";
    public ContextDoc? Context { get; set; }

    /// <summary>Unix seconds; null = permanent. Expired entries are ignored and removed by the expiry scheduler.</summary>
    public long? Expires { get; set; }
}

/// <summary>A permission rule: a node (exact, "a.b.*" or "*") with a value, an optional context pattern and an optional expiry.</summary>
public sealed class RuleDoc
{
    public string Node { get; set; } = "";
    public bool Value { get; set; } = true;
    public ContextDoc? Context { get; set; }
    public long? Expires { get; set; }
}

public sealed class MetaDoc
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public ContextDoc? Context { get; set; }
    public long? Expires { get; set; }
}

public sealed class AffixDoc
{
    public string Text { get; set; } = "";

    /// <summary>Higher priority shows first.</summary>
    public int Priority { get; set; }

    public ContextDoc? Context { get; set; }
    public long? Expires { get; set; }
}

/// <summary>A context pattern: null or absent position = any. Names, not bytes; Core maps them to the native tuple.</summary>
public sealed class ContextDoc
{
    /// <summary>World name as the server names it (e.g. "minecraft:overworld" or a runtime world name).</summary>
    public string? World { get; set; }

    /// <summary>"overworld", "the_nether", "the_end" or "other".</summary>
    public string? Dimension { get; set; }

    /// <summary>"survival", "creative", "adventure", "spectator".</summary>
    public string? Gamemode { get; set; }

    public int? Server { get; set; }

    /// <summary>Slot 0 / slot 1 value names (declared in StoreDoc.Slots).</summary>
    public string? Slot0 { get; set; }
    public string? Slot1 { get; set; }

    [JsonIgnore]
    public bool IsAny => World is null && Dimension is null && Gamemode is null && Server is null && Slot0 is null && Slot1 is null;
}

public sealed class TrackDoc
{
    public List<string> Groups { get; set; } = [];
}

public sealed class ContextSlotDoc
{
    public string Key { get; set; } = "";

    /// <summary>At most 15 values.</summary>
    public List<string> Values { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(StoreDoc))]
public sealed partial class StoreJson : JsonSerializerContext;
