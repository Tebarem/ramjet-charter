using Charter.Model;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using Mc.Scripting.Permissions;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>The core: the store state, the change pipeline, the native table feed, expiry and the resolution entry points. One instance per plugin load.
/// Every change goes through <see cref="Change"/>: a deep copy is edited, validated, saved atomically, diffed against the old document, pushed to the table,
/// reported through <see cref="Changed"/> and audited. A failed step leaves the previous state in place.</summary>
public sealed class CharterCore : ICharterCore
{
    /// <summary>Changes per second one actor may make (the system's expiry is exempt).</summary>
    public const int RateLimitPerSecond = 20;

    // above this many touched users, listeners get one "everyone" notification instead of one per user
    private const int ManyChanged = 128;

    private readonly string _dataFolder;
    private readonly Logger _log;
    private readonly TableSync _table;
    private readonly ExpiryScheduler _expiry;
    private readonly Dictionary<string, Queue<long>> _recent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedNotes = new(StringComparer.Ordinal);
    private StoreDoc _doc = StoreFile.CreateEmpty();
    private string? _loadError;
    private string? _lastError;
    private bool _started;
    private bool _contextBound;
    private long _changes;
    private long _rejected;
    private long _rateLimited;
    private long _reloads;

    public CharterCore(string dataFolder, Scheduler scheduler, Logger log)
    {
        _dataFolder = dataFolder;
        _log = log;
        _table = new TableSync(this, scheduler, log);
        _expiry = new ExpiryScheduler(this, scheduler);
    }

    /// <summary>Where audit records go (set by the plugin right after the core is created; null until then).</summary>
    public IAuditSink? Audit { get; set; }

    /// <summary>Called once when the store is loaded and the core starts: the plugin installs <see cref="CharterProvider"/> here, so the provider is only
    /// active while the store is usable.</summary>
    public Action? OnStarted { get; set; }

    /// <inheritdoc/>
    public string DataFolder => _dataFolder;

    /// <inheritdoc/>
    public StoreDoc Current => _doc;

    /// <summary>Why the store is not usable (null when it is). While set, nothing is written.</summary>
    public string? LoadError => _loadError;

    /// <summary>Whether the core is running (store loaded, table claimed, provider installed).</summary>
    public bool Started => _started;

    /// <inheritdoc/>
    public event Action<Uuid?>? Changed;

    /// <summary>Reads the store once, at enable. An error text (and no document) is returned; the caller logs it loudly and does not call <see cref="Start"/>.</summary>
    public string? Load()
    {
        StoreLoad loaded = StoreFile.Load(_dataFolder);
        foreach (string note in loaded.Notes) _log.Info(note);
        if (loaded.Doc is null)
        {
            _loadError = loaded.Error ?? "the store could not be read";
            _lastError = _loadError;
            return _loadError;
        }
        _doc = loaded.Doc;
        _loadError = null;
        return null;
    }

    /// <summary>Starts a loaded core: claims the table, sends the snapshot, installs the provider (<see cref="OnStarted"/>), schedules expiry.</summary>
    /// <returns>Whether the engine accepted the claim.</returns>
    public bool Start()
    {
        if (_started || _loadError is not null) return _started;
        _started = true;
        _contextBound = HasContextBound(_doc);
        bool claimed = _table.Claim();
        OnStarted?.Invoke();
        _table.Snapshot();
        _expiry.Reschedule();
        Changed?.Invoke(null);
        return claimed;
    }

    /// <inheritdoc/>
    public ChangeResult Change(Actor actor, string action, string? detail, Func<StoreDoc, string?> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_loadError is not null) return Refuse($"the store is not loaded ({_loadError}); fix store.json and run /charter reload");
        if (!AllowChange(actor)) return Refuse($"too many changes: more than {RateLimitPerSecond} per second from {actor.Name}");

        StoreDoc next;
        try { next = StoreFile.DeepCopy(_doc); }
        catch (Exception e) { return Refuse($"could not copy the store: {e.Message}"); }

        string? error;
        try { error = edit(next); }
        catch (Exception e) { return Refuse($"the change failed: {e.Message}"); }
        if (error is not null) return Refuse(error);

        StoreFile.Normalize(next, new List<string>());
        string? invalid = StoreFile.Validate(next);
        if (invalid is not null) return Refuse(invalid);
        string? saved = StoreFile.Save(_dataFolder, next);
        if (saved is not null) return Refuse(saved);

        StoreDoc old = _doc;
        _doc = next;
        _changes++;
        (bool groupLevel, List<Uuid> users) = Diff(old, next);
        int touched = Committed(groupLevel, users);
        Record(actor, action, null, detail);
        return new ChangeResult(true, null, touched);
    }

    /// <inheritdoc/>
    public ChangeResult Reload()
    {
        StoreLoad loaded = StoreFile.Load(_dataFolder);
        foreach (string note in loaded.Notes) _log.Info(note);
        if (loaded.Doc is null)
        {
            string error = loaded.Error ?? "the store could not be read";
            _lastError = error;
            return ChangeResult.Fail(error);
        }

        StoreDoc old = _doc;
        bool wasLoaded = _loadError is null;
        _doc = loaded.Doc;
        _loadError = null;
        _reloads++;
        if (!_started)
        {
            Start();
            Record(Actor.System, "reload", null, "store loaded; charter started");
            return new ChangeResult(true, null, _doc.Users.Count);
        }
        (bool groupLevel, List<Uuid> users) = wasLoaded ? Diff(old, _doc) : (true, new List<Uuid>());
        int touched = Committed(groupLevel, users);
        Record(Actor.System, "reload", null, null);
        return new ChangeResult(true, null, touched);
    }

    /// <summary>Removes every entry that expired at <paramref name="now"/> (one audited change, or nothing when none expired).</summary>
    internal ChangeResult ExpireUntil(long now)
    {
        int count = ExpiryScheduler.CountExpired(_doc, now);
        if (count == 0) return new ChangeResult(true, null, 0);
        return Change(Actor.System, "expire", $"{count} expired entries removed", doc =>
        {
            ExpiryScheduler.RemoveExpired(doc, now);
            return null;
        });
    }

    /// <inheritdoc/>
    public Resolution Resolve(Uuid user, string node, QueryContext ctx, StoreDoc? doc = null)
        => Resolver.Resolve(doc ?? _doc, user, node, ctx, NowSeconds());

    /// <inheritdoc/>
    /// <remarks>Break-glass: an operator of level 4 has every node. An unknown or unregistered node is denied, except a vanilla command node, whose default
    /// is the command's operator level (the engine's own default, asked through the permission manager while the context is the player's own).</remarks>
    public bool Has(Uuid user, string node, QueryContext ctx, StoreDoc? doc = null)
    {
        int level = OpLevel(user);
        if (level >= 4) return true;
        Resolution r = Resolve(user, node, ctx, doc);
        if (r.Value != PermissionValue.Unset) return r.Value == PermissionValue.True;
        return DefaultFor(user, node, ctx, doc, level);
    }

    /// <inheritdoc/>
    public QueryContext ContextOf(Uuid user)
    {
        Player? online = Server.GetPlayer(user);
        if (online is not { } player) return new QueryContext(null, null, null, _doc.ServerId, null, null);
        string world = player.World.Key;
        return new QueryContext(world, ContextMap.DimensionOf(world), player.GameMode.ToString().ToLowerInvariant(), _doc.ServerId, null, null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> EffectiveGroups(Uuid user, QueryContext ctx, StoreDoc? doc = null)
        => Resolver.EffectiveGroups(doc ?? _doc, user, ctx, NowSeconds());

    /// <inheritdoc/>
    public bool TryFindUser(string nameOrUuid, out Uuid user, out string displayName)
    {
        user = default;
        displayName = nameOrUuid;
        if (string.IsNullOrWhiteSpace(nameOrUuid)) return false;
        string text = nameOrUuid.Trim();

        if (UuidText.TryParse(text, out Uuid parsed))
        {
            user = parsed;
            displayName = Server.GetPlayer(parsed)?.Name ?? StoredName(parsed) ?? OfflinePlayers.Get(parsed)?.Name ?? text;
            return true;
        }
        if (Server.GetPlayer(text) is { } online)
        {
            user = online.Uuid;
            displayName = online.Name;
            return true;
        }
        foreach (var (key, record) in _doc.Users)
        {
            if (record?.Name is { } name && name.Equals(text, StringComparison.OrdinalIgnoreCase) && UuidText.TryParse(key, out Uuid stored))
            {
                user = stored;
                displayName = name;
                return true;
            }
        }
        if (OfflinePlayers.Find(text) is { } found)
        {
            user = found;
            displayName = OfflinePlayers.Get(found)?.Name is { Length: > 0 } offlineName ? offlineName : text;
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Key, string Value)> Status()
    {
        var rows = new List<(string Key, string Value)>
        {
            ("state", _loadError is not null ? "store error" : _started ? "running" : "idle"),
            ("store", Path.Combine(_dataFolder, StoreFile.FileName)),
            ("schema", _doc.SchemaVersion.ToString()),
            ("server_id", _doc.ServerId.ToString()),
            ("users", _doc.Users.Count.ToString()),
            ("groups", _doc.Groups.Count.ToString()),
            ("tracks", _doc.Tracks.Count.ToString()),
            ("changes", _changes.ToString()),
            ("rejected", _rejected.ToString()),
            ("rate_limited", _rateLimited.ToString()),
            ("reloads", _reloads.ToString()),
            ("context_bound", _contextBound ? "yes" : "no"),
            ("load_error", _loadError ?? "none"),
            ("last_error", _lastError ?? "none"),
            ("next_expiry", _expiry.NextDue is { } due ? DateTimeOffset.FromUnixTimeSeconds(due).ToString("u") : "none"),
            ("table_claimed", _table.Claimed ? "yes" : "no"),
            ("table_subjects_pushed", _table.Subjects.ToString()),
            ("table_refused_messages", _table.RefusedMessages.ToString()),
            ("table_resync_scheduled", _table.Retries.ToString()),
        };
        NativeTableStats? stats = NativePermissionTable.Stats();
        if (stats is { } s)
        {
            rows.Add(("engine_claimed", s.Claimed ? "yes" : "no"));
            rows.Add(("engine_subjects", s.Subjects.ToString()));
            rows.Add(("engine_rules", s.Rules.ToString()));
            rows.Add(("engine_nodes", s.Nodes.ToString()));
            rows.Add(("engine_applied", s.Applied.ToString()));
            rows.Add(("engine_refused", s.Rejected.ToString()));
            rows.Add(("engine_generation", s.Generation.ToString()));
            rows.Add(("engine_last_status", s.LastStatus.ToString()));
        }
        else rows.Add(("engine_table", "unavailable"));
        return rows;
    }

    /// <summary>The explicit value the provider reports: break-glass first, then the resolver at the player's context (Unset when no rule).</summary>
    public PermissionValue ExplicitValue(Uuid player, string node)
    {
        if (OpLevel(player) >= 4) return PermissionValue.True;
        return Resolve(player, node, ContextOf(player)).Value;
    }

    /// <summary>A player joined: the table gets the subject if it does not hold it.</summary>
    public void OnPlayerJoined(Uuid user)
    {
        if (_started) _table.Join(user);
    }

    /// <summary>A player left: the table drops the subject.</summary>
    public void OnPlayerLeft(Uuid user)
    {
        if (_started) _table.Quit(user);
    }

    /// <summary>A player's world or game mode changed: rules, meta and affixes bound to a context may apply differently now.</summary>
    public void OnContextChanged(Uuid user)
    {
        if (_started && _contextBound) Changed?.Invoke(user);
    }

    /// <summary>The compiled rules of one subject now (for the table).</summary>
    internal List<NativeRule> CompileSubject(Uuid user, ContextMap map, List<string> notes)
        => Compiler.Compile(_doc, user, NowSeconds(), map, notes);

    /// <summary>Writes notes once each to the log (a rule that names a missing world is reported at every compile otherwise).</summary>
    internal void LogNotes(List<string> notes)
    {
        foreach (string note in notes)
            if (_loggedNotes.Add(note)) _log.Warn(note);
    }

    /// <summary>The current unix time in seconds (expiry and resolution use it, never the wall clock inside a test's input).</summary>
    internal static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private bool DefaultFor(Uuid user, string node, QueryContext ctx, StoreDoc? doc, int level)
    {
        if (PermissionManager.IsRegistered(node))
        {
            foreach (Permission permission in PermissionManager.Registered)
                if (permission.Node == node) return permission.Default.Evaluate(level);
        }
        // TODO(engine): a vanilla command's default level is not public (Host.VanillaCommandLevel). The permission manager answers it for an online player
        // and for offline ones through the uuid's level; asking it here is only right while the context is the player's own.
        if (node.StartsWith("minecraft.command.", StringComparison.Ordinal) && doc is null && ctx == ContextOf(user))
            return PermissionManager.Has(user, node);
        return false;
    }

    // TODO(engine): an offline player's operator level has no public API (PermissionManager.Has(uuid) reads it internally). Offline players are not
    // checked against commands, so 0 is right for every current caller.
    private static int OpLevel(Uuid user) => Server.GetPlayer(user) is { } player ? player.PermissionLevel : 0;

    private string? StoredName(Uuid user)
        => _doc.Users.TryGetValue(user.ToString(), out UserDoc? record) && record?.Name is { Length: > 0 } name ? name : null;

    private bool AllowChange(Actor actor)
    {
        if (actor.Kind == "system") return true;
        long nowMs = Environment.TickCount64;
        string key = actor.Kind + ":" + actor.Name;
        if (!_recent.TryGetValue(key, out Queue<long>? times)) _recent[key] = times = new Queue<long>();
        while (times.Count > 0 && nowMs - times.Peek() >= 1000) times.Dequeue();
        if (times.Count >= RateLimitPerSecond)
        {
            _rateLimited++;
            return false;
        }
        times.Enqueue(nowMs);
        return true;
    }

    private ChangeResult Refuse(string error)
    {
        _rejected++;
        _lastError = error;
        return ChangeResult.Fail(error);
    }

    private void Record(Actor actor, string action, string? subject, string? detail)
    {
        try { Audit?.Append(new AuditEntry(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), actor, action, subject, detail)); }
        catch (Exception e) { _log.Error("could not write the audit record", e); }
    }

    // after a commit or a reload: the table gets the subjects whose rules changed, expiry is rescheduled, listeners hear about the users concerned
    private int Committed(bool groupLevel, List<Uuid> users)
    {
        _contextBound = HasContextBound(_doc);
        List<Uuid> pushed = _started ? _table.Sync() : new List<Uuid>();
        if (_started) _expiry.Reschedule();
        var touched = new HashSet<Uuid>(users);
        touched.UnionWith(pushed);
        if (groupLevel || touched.Count > ManyChanged) Changed?.Invoke(null);
        else
        {
            foreach (Uuid user in touched) Changed?.Invoke(user);
        }
        return groupLevel ? _doc.Users.Count : touched.Count;
    }

    // what changed between two documents: a group-level change (groups, tracks, slots, server id) concerns everyone; otherwise the users whose entries differ
    private static (bool GroupLevel, List<Uuid> Users) Diff(StoreDoc a, StoreDoc b)
    {
        if (a.ServerId != b.ServerId || !SlotsEqual(a.Slots, b.Slots) || !TracksEqual(a.Tracks, b.Tracks) || !GroupsEqual(a.Groups, b.Groups))
            return (true, new List<Uuid>());
        var users = new List<Uuid>();
        foreach (string key in a.Users.Keys.Union(b.Users.Keys, StringComparer.Ordinal))
        {
            a.Users.TryGetValue(key, out UserDoc? before);
            b.Users.TryGetValue(key, out UserDoc? after);
            if (UserJson(before) == UserJson(after)) continue;
            if (UuidText.TryParse(key, out Uuid uuid)) users.Add(uuid);
        }
        return (false, users);
    }

    private static bool GroupsEqual(Dictionary<string, GroupDoc> a, Dictionary<string, GroupDoc> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (name, group) in a)
            if (!b.TryGetValue(name, out GroupDoc? other) || GroupJson(group) != GroupJson(other)) return false;
        return true;
    }

    private static bool TracksEqual(Dictionary<string, TrackDoc> a, Dictionary<string, TrackDoc> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (name, track) in a)
            if (!b.TryGetValue(name, out TrackDoc? other) || TrackJson(track) != TrackJson(other)) return false;
        return true;
    }

    private static bool SlotsEqual(List<ContextSlotDoc> a, List<ContextSlotDoc> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i]?.Key != b[i]?.Key) return false;
            List<string> av = a[i]?.Values ?? new List<string>();
            List<string> bv = b[i]?.Values ?? new List<string>();
            if (!av.SequenceEqual(bv)) return false;
        }
        return true;
    }

    private static string UserJson(UserDoc? user) => user is null ? "" : JsonSerializer.Serialize(user, StoreCompareJson.Default.UserDoc);

    private static string GroupJson(GroupDoc? group) => group is null ? "" : JsonSerializer.Serialize(group, StoreCompareJson.Default.GroupDoc);

    private static string TrackJson(TrackDoc? track) => track is null ? "" : JsonSerializer.Serialize(track, StoreCompareJson.Default.TrackDoc);

    private static bool HasContextBound(StoreDoc doc)
    {
        static bool Bound(ContextDoc? c) => c is not null && (c.World is not null || c.Dimension is not null || c.Gamemode is not null
            || c.Slot0 is not null || c.Slot1 is not null);
        foreach (UserDoc? user in doc.Users.Values)
        {
            if (user is null) continue;
            if (user.Groups.Any(m => m is not null && Bound(m.Context)) || user.Rules.Any(r => r is not null && Bound(r.Context))
                || user.Meta.Any(m => m is not null && Bound(m.Context)) || user.Prefixes.Any(a => a is not null && Bound(a.Context))
                || user.Suffixes.Any(a => a is not null && Bound(a.Context)))
                return true;
        }
        foreach (GroupDoc? group in doc.Groups.Values)
        {
            if (group is null) continue;
            if (group.Parents.Any(m => m is not null && Bound(m.Context)) || group.Rules.Any(r => r is not null && Bound(r.Context))
                || group.Meta.Any(m => m is not null && Bound(m.Context)) || group.Prefixes.Any(a => a is not null && Bound(a.Context))
                || group.Suffixes.Any(a => a is not null && Bound(a.Context)))
                return true;
        }
        return false;
    }
}
