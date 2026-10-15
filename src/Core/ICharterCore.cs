using Mc.Scripting;
using Mc.Scripting.Permissions;
using Charter.Model;

namespace Charter.Core;

// The seam between Core (store, resolver, table sync, provider, meta, expiry) and its users (commands, audit, import).
// Commands and import never touch the native table, the store file or the provider directly: they mutate a copy of the
// document through Change and let Core validate, save, recompile, push and audit it.

/// <summary>Who made a change (for the audit log and rate limits).</summary>
public readonly record struct Actor(string Kind, string Name, Uuid? Id)
{
    public static Actor Console => new("console", "console", null);
    public static Actor Player(Player p) => new("player", p.Name, p.Uuid);
    public static Actor Import(string source) => new("import", source, null);
    public static Actor System => new("system", "charter", null);
}

/// <summary>A query context (what a check is asked for). Null position = unknown (matches only "any" patterns).</summary>
public sealed record QueryContext(string? World, string? Dimension, string? Gamemode, int? Server, string? Slot0, string? Slot1)
{
    public static readonly QueryContext Unknown = new(null, null, null, null, null, null);
}

/// <summary>Why a node resolved the way it did (for /charter user check and preview).</summary>
/// <param name="Value">True/False, or Unset when no rule matched (the caller applies the node default).</param>
/// <param name="Layer">0 = the user's own rules, 1.. = groups in layer order; -1 when Unset.</param>
/// <param name="Source">"user" or the group name; null when Unset.</param>
/// <param name="Rule">The rule that decided; null when Unset.</param>
public sealed record Resolution(PermissionValue Value, int Layer, string? Source, RuleDoc? Rule);

/// <summary>Result of a change attempt.</summary>
public sealed record ChangeResult(bool Ok, string? Error, int SubjectsChanged)
{
    public static ChangeResult Fail(string error) => new(false, error, 0);
}

/// <summary>One audit record as passed to the audit sink (Core calls it after a successful save).</summary>
public sealed record AuditEntry(long Time, Actor Actor, string Action, string? Subject, string? Detail);

/// <summary>Where audit records go. Implemented by Audit/AuditLog.cs; Core holds it and calls Append after every saved change.</summary>
public interface IAuditSink
{
    void Append(AuditEntry entry);
}

public interface ICharterCore
{
    /// <summary>The current document (read-only view: do not mutate; use Change).</summary>
    StoreDoc Current { get; }

    /// <summary>Applies a change: <paramref name="edit"/> gets a deep copy of the document and returns null to commit or an error text to abort.
    /// Core validates the result (names, node syntax, counts, cycles reported but tolerated), saves it atomically (store.json.bak kept),
    /// recompiles the changed subjects, pushes them to the native table, reschedules expiries, raises Changed and writes one audit entry
    /// with <paramref name="action"/> and <paramref name="detail"/>. A failed validation or save leaves everything as it was.</summary>
    ChangeResult Change(Actor actor, string action, string? detail, Func<StoreDoc, string?> edit);

    /// <summary>Re-reads store.json (the file is canonical). A malformed file is an error and the previous state stays.</summary>
    ChangeResult Reload();

    /// <summary>Pure resolution against <paramref name="doc"/> (the current document when null): the D3 order, no defaults applied.</summary>
    Resolution Resolve(Uuid user, string node, QueryContext ctx, StoreDoc? doc = null);

    /// <summary>The effective value: Resolve, then the registered node default at the user's op level (break-glass: op 4 is always True).</summary>
    bool Has(Uuid user, string node, QueryContext ctx, StoreDoc? doc = null);

    /// <summary>The query context of an online player (world, dimension, gamemode, server id, slots); Unknown with the server id when offline.</summary>
    QueryContext ContextOf(Uuid user);

    /// <summary>The user's groups in layer order (own groups by weight, parents after children, "default" last), expired memberships skipped.</summary>
    IReadOnlyList<string> EffectiveGroups(Uuid user, QueryContext ctx, StoreDoc? doc = null);

    /// <summary>Uuid of a user by name: online players first, then the store's last known names, then the engine's offline profiles.</summary>
    bool TryFindUser(string nameOrUuid, out Uuid user, out string displayName);

    /// <summary>Native table statistics (perm_stats) and Core's own counters, for /charter status.</summary>
    IReadOnlyList<(string Key, string Value)> Status();

    /// <summary>Raised after a committed change or reload: the user whose effective state changed, or null for many.</summary>
    event Action<Uuid?>? Changed;

    /// <summary>Data folder (plugins/charter/data); the only place the plugin may write or read files.</summary>
    string DataFolder { get; }
}

/// <summary>Uuid text helpers (the API's Uuid has no parser).</summary>
public static class UuidText
{
    public static bool TryParse(string text, out Uuid uuid)
    {
        uuid = default;
        string hex = text.Replace("-", "", StringComparison.Ordinal);
        if (hex.Length != 32) return false;
        if (!ulong.TryParse(hex.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out ulong most)) return false;
        if (!ulong.TryParse(hex.AsSpan(16, 16), System.Globalization.NumberStyles.HexNumber, null, out ulong least)) return false;
        uuid = new Uuid(most, least);
        return true;
    }

    public static string Format(Uuid uuid) => uuid.ToString();
}
