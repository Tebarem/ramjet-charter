using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Charter.Model;
using Mc.Scripting;

namespace Charter.Import;

/// <summary>Imports a LuckPerms export file: the gzip-compressed JSON its export command writes, or plain JSON.
/// Groups and users come from their "nodes" lists. Node keys: group.X is a membership (a parent for a group); weight.N the group weight;
/// prefix.P.text and suffix.P.text affixes; meta.K.V meta; displayname.N the display name of a group; r=... and R=... regex nodes are skipped
/// (D12); anything else is a rule, with a leading "-" meaning a denial. Escaped dots in meta and affix text are unescaped.
/// Contexts: world, gamemode, server (a numeric id 0-255, or "global" = any server), dimension and dimension-type. A key with several values
/// yields one entry per value. An entry whose context cannot be represented is skipped with its reason, never imported without the context.
/// Expired entries are skipped. A user with nothing beyond default membership is skipped and counted. Tracks keep only groups in the result.</summary>
public static class LuckPermsImport
{
    /// <summary>Reads <paramref name="path"/> and merges it into a copy of <paramref name="current"/>, with the clock at the current time.</summary>
    /// <exception cref="InvalidDataException">The file is not a LuckPerms export, or its uncompressed JSON is larger than 64 MiB.</exception>
    public static ImportReport Run(string path, StoreDoc current) => RunAt(path, current, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>As <see cref="Run"/>, with the clock given (unix seconds) for the expiry checks.</summary>
    internal static ImportReport RunAt(string path, StoreDoc current, long now)
    {
        var report = new ImportReport();
        StoreDoc result = ImportSupport.DeepCopy(current);
        report.Result = result;

        using JsonDocument json = ParseExport(path);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: not a LuckPerms export (the top level is not an object)");

        var session = new Session(report, result, now);
        JsonElement tracks = default;
        bool hasTracks = false;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "groups":
                    session.ImportGroups(p.Value);
                    break;
                case "users":
                    session.ImportUsers(p.Value);
                    break;
                case "tracks":
                    tracks = p.Value;
                    hasTracks = true;
                    break;
                default:
                    report.AddNote($"{path}: top-level '{p.Name}' is not part of the data model; ignored");
                    break;
            }
        }

        ImportSupport.EnsureDefault(result);
        if (hasTracks) session.ImportTracks(tracks);
        ImportSupport.ReportDanglingMemberships(result, report);
        session.Summarize(path);
        return report;
    }

    /// <summary>Opens the file, decompresses it when it starts with the gzip magic, and parses it with a 64 MiB cap on the decompressed size.</summary>
    private static JsonDocument ParseExport(string path)
    {
        using FileStream file = File.OpenRead(path);
        var head = new byte[2];
        int got = file.Read(head, 0, head.Length);
        file.Position = 0;
        bool gzip = got == 2 && head[0] == 0x1F && head[1] == 0x8B;
        using Stream decoded = gzip ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : file;
        using var limited = new LimitedStream(decoded, ImportLimits.MaxLuckPermsJsonBytes, path);
        try
        {
            return JsonDocument.Parse(limited, ImportSupport.JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: not a LuckPerms export ({e.Message})", e);
        }
    }

    private enum ContextField
    {
        None,
        World,
        Dimension,
        Gamemode,
        Server,
    }

    /// <summary>What one group or user collects from its nodes.</summary>
    private sealed class Target
    {
        public Target(string label, bool isUser)
        {
            Label = label;
            IsUser = isUser;
        }

        public string Label { get; }
        public bool IsUser { get; }
        public int? Weight { get; set; }
        public string? DisplayName { get; set; }
        public List<Membership> Memberships { get; } = [];
        public HashSet<string> MembershipKeys { get; } = new(StringComparer.Ordinal);
        public List<RuleDoc> Rules { get; } = [];
        public List<MetaDoc> Meta { get; } = [];
        public List<AffixDoc> Prefixes { get; } = [];
        public List<AffixDoc> Suffixes { get; } = [];
        public bool RulesCapped { get; set; }

        public bool IsEmpty => Memberships.Count == 0 && Rules.Count == 0 && Meta.Count == 0 && Prefixes.Count == 0 && Suffixes.Count == 0;
    }

    private sealed class Session
    {
        private readonly ImportReport _report;
        private readonly StoreDoc _result;
        private readonly long _now;
        private int _groups;
        private int _users;
        private int _rules;
        private int _expired;
        private int _regex;
        private int _dropped;
        private int _noData;

        public Session(ImportReport report, StoreDoc result, long now)
        {
            _report = report;
            _result = result;
            _now = now;
        }

        public void ImportGroups(JsonElement groups)
        {
            if (groups.ValueKind != JsonValueKind.Object)
            {
                Skip("groups: not an object, no groups read");
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in groups.EnumerateObject())
            {
                string name = ImportSupport.LowerName(p.Name);
                if (name != p.Name) Remap($"group '{p.Name}' -> '{name}'");
                if (seen.Contains(name))
                {
                    Skip($"group '{p.Name}': same name as an earlier group after lower-casing; the earlier one is kept");
                    continue;
                }
                if (seen.Count >= ImportLimits.MaxGroups)
                {
                    Skip($"groups: more than {ImportLimits.MaxGroups}; the rest are skipped");
                    break;
                }
                seen.Add(name);
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    Skip($"group '{name}': not an object");
                    continue;
                }
                var target = new Target($"group '{name}'", isUser: false);
                ReadNodes(p.Value, target);
                var group = new GroupDoc
                {
                    Weight = target.Weight ?? 0,
                    DisplayName = target.DisplayName,
                    Parents = target.Memberships,
                    Rules = target.Rules,
                    Meta = target.Meta,
                    Prefixes = target.Prefixes,
                    Suffixes = target.Suffixes,
                };
                if (_result.Groups.ContainsKey(name)) Note($"group '{name}' replaces the one in the store");
                _result.Groups[name] = group;
                _groups++;
            }
        }

        public void ImportUsers(JsonElement users)
        {
            if (users.ValueKind != JsonValueKind.Object)
            {
                Skip("users: not an object, no users read");
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in users.EnumerateObject())
            {
                if (!ImportSupport.TryNormalizeUuid(p.Name, out string id))
                {
                    Skip($"user '{p.Name}': the key is not a uuid");
                    continue;
                }
                if (id != p.Name) Remap($"user '{p.Name}' -> '{id}'");
                if (seen.Contains(id))
                {
                    Skip($"user '{p.Name}': same uuid as an earlier entry; the earlier one is kept");
                    continue;
                }
                if (seen.Count >= ImportLimits.MaxUsers)
                {
                    Skip($"users: more than {ImportLimits.MaxUsers}; the rest are skipped");
                    break;
                }
                seen.Add(id);
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    Skip($"user '{id}': not an object");
                    continue;
                }

                string? username = StringField(p.Value, "username") ?? StringField(p.Value, "name");
                string? primary = StringField(p.Value, "primaryGroup");
                var target = new Target($"user '{id}'", isUser: true);
                ReadNodes(p.Value, target);
                if (primary is not null)
                {
                    string primaryName = ImportSupport.LowerName(primary);
                    if (primaryName != primary) Remap($"user '{id}': primary group '{primary}' -> '{primaryName}'");
                    if (primaryName != ImportSupport.DefaultGroup && !target.Memberships.Exists(m => m.Group == primaryName))
                    {
                        target.Memberships.Add(new Membership { Group = primaryName });
                        Note($"user '{id}': primary group '{primaryName}' imported as a membership (check against LuckPerms)");
                    }
                }
                if (target.IsEmpty)
                {
                    _noData++;
                    continue;
                }

                var user = new UserDoc
                {
                    Name = username,
                    Groups = target.Memberships,
                    Rules = target.Rules,
                    Meta = target.Meta,
                    Prefixes = target.Prefixes,
                    Suffixes = target.Suffixes,
                };
                if (_result.Users.ContainsKey(id)) Note($"user '{id}' replaces the one in the store");
                _result.Users[id] = user;
                _users++;
            }
        }

        public void ImportTracks(JsonElement tracks)
        {
            if (tracks.ValueKind != JsonValueKind.Object)
            {
                Skip("tracks: not an object, no tracks read");
                return;
            }
            foreach (JsonProperty p in tracks.EnumerateObject())
            {
                string name = ImportSupport.LowerName(p.Name);
                if (name != p.Name) Remap($"track '{p.Name}' -> '{name}'");
                JsonElement list = default;
                bool hasList = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("groups", out list) && list.ValueKind == JsonValueKind.Array;
                if (!hasList)
                {
                    Skip($"track '{name}': no groups array");
                    continue;
                }
                var groups = new List<string>();
                foreach (string raw in ImportSupport.StringList(list, _report, $"track '{name}'"))
                {
                    string group = ImportSupport.LowerName(raw);
                    if (group != raw) Remap($"track '{name}': group '{raw}' -> '{group}'");
                    if (!_result.Groups.ContainsKey(group))
                    {
                        Skip($"track '{name}': group '{raw}' is not in the store; dropped from the track");
                        continue;
                    }
                    if (!groups.Contains(group)) groups.Add(group);
                }
                if (_result.Tracks.ContainsKey(name)) Note($"track '{name}' replaces the one in the store");
                _result.Tracks[name] = new TrackDoc { Groups = groups };
            }
        }

        public void Summarize(string path)
        {
            Note($"{path}: {_groups} groups, {_users} users, {_rules} rules read; {_expired} expired entries and {_regex} regex nodes skipped; "
                + $"{_dropped} implicit default memberships dropped; {_noData} users with nothing beyond default skipped");
        }

        private void Skip(string text) => _report.AddSkipped(text);
        private void Remap(string text) => _report.AddRemapped(text);
        private void Note(string text) => _report.AddNote(text);

        private void ReadNodes(JsonElement owner, Target target)
        {
            if (!owner.TryGetProperty("nodes", out JsonElement nodes)) return;
            if (nodes.ValueKind != JsonValueKind.Array)
            {
                Skip($"{target.Label}: nodes is not an array");
                return;
            }
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    Skip($"{target.Label}: a node is not an object");
                    continue;
                }
                ReadNode(node, target);
            }
        }

        private void ReadNode(JsonElement node, Target t)
        {
            string? key = node.TryGetProperty("key", out JsonElement keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
            if (string.IsNullOrEmpty(key))
            {
                Skip($"{t.Label}: a node has no key");
                return;
            }

            bool value = true;
            if (node.TryGetProperty("value", out JsonElement valueEl))
            {
                if (valueEl.ValueKind == JsonValueKind.True) value = true;
                else if (valueEl.ValueKind == JsonValueKind.False) value = false;
                else
                {
                    Skip($"{t.Label} '{key}': value is not a boolean");
                    return;
                }
            }

            long? expires = null;
            if (node.TryGetProperty("expiry", out JsonElement expiryEl) && expiryEl.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadSeconds(expiryEl, out long seconds))
                {
                    Skip($"{t.Label} '{key}': expiry is not a number");
                    return;
                }
                if (seconds < _now)
                {
                    _expired++;
                    Skip($"{t.Label} '{key}': expired at {seconds}, skipped");
                    return;
                }
                expires = seconds;
            }

            var contexts = new List<ContextDoc?>();
            if (node.TryGetProperty("context", out JsonElement contextEl) && contextEl.ValueKind != JsonValueKind.Null)
            {
                string? problem = ExpandContext(contextEl, contexts);
                if (problem is not null)
                {
                    Skip($"{t.Label} '{key}': {problem}");
                    return;
                }
            }
            else
            {
                contexts.Add(null);
            }

            if (key.StartsWith("r=", StringComparison.Ordinal) || key.StartsWith("R=", StringComparison.Ordinal))
            {
                _regex++;
                Skip($"{t.Label} '{key}': regex node, not supported (D12)");
            }
            else if (key.StartsWith("group.", StringComparison.Ordinal)) ReadMembership(key, value, expires, contexts, t);
            else if (key.StartsWith("weight.", StringComparison.Ordinal)) ReadWeight(key, value, expires, contexts, t);
            else if (key.StartsWith("prefix.", StringComparison.Ordinal) || key.StartsWith("suffix.", StringComparison.Ordinal)) ReadAffix(key, value, expires, contexts, t);
            else if (key.StartsWith("meta.", StringComparison.Ordinal)) ReadMeta(key, value, expires, contexts, t);
            else if (key.StartsWith("displayname.", StringComparison.Ordinal)) ReadDisplayName(key, value, expires, contexts, t);
            else ReadRule(key, value, expires, contexts, t);
        }

        private void ReadMembership(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            string[]? fields = ImportSupport.SplitFields(key, 2);
            if (fields is null || fields[1].Length == 0)
            {
                Skip($"{t.Label} '{key}': no group name");
                return;
            }
            if (!value)
            {
                Skip($"{t.Label} '{key}': a negated membership cannot be represented");
                return;
            }
            string group = ImportSupport.LowerName(fields[1]);
            if (group != fields[1]) Remap($"{t.Label}: membership '{fields[1]}' -> '{group}'");
            if (group == ImportSupport.DefaultGroup)
            {
                _dropped++;
                return;
            }
            foreach (ContextDoc? ctx in contexts) ImportSupport.AddMembership(t.Memberships, t.MembershipKeys, group, ctx, expires);
        }

        private void ReadWeight(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            if (t.IsUser)
            {
                Skip($"{t.Label} '{key}': a weight on a user has no meaning");
                return;
            }
            if (!value || expires is not null || contexts.Count != 1 || contexts[0] is not null)
            {
                Skip($"{t.Label} '{key}': only a permanent, context-free, positive weight can be imported");
                return;
            }
            string[]? fields = ImportSupport.SplitFields(key, 2);
            if (fields is null || !int.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int weight))
            {
                Skip($"{t.Label} '{key}': weight is not an integer");
                return;
            }
            if (t.Weight is int previous && previous != weight) Note($"{t.Label}: several weights; the last one wins");
            t.Weight = weight;
        }

        private void ReadAffix(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            bool prefix = key.StartsWith("prefix.", StringComparison.Ordinal);
            string[]? fields = ImportSupport.SplitFields(key, 3);
            if (fields is null || !int.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int priority))
            {
                Skip($"{t.Label} '{key}': expected {(prefix ? "prefix" : "suffix")}.<priority>.<text>");
                return;
            }
            if (!value)
            {
                Skip($"{t.Label} '{key}': a negated affix cannot be represented");
                return;
            }
            if (fields[2].Length == 0)
            {
                Skip($"{t.Label} '{key}': empty affix text");
                return;
            }
            foreach (ContextDoc? ctx in contexts)
            {
                var affix = new AffixDoc { Text = fields[2], Priority = priority, Context = ctx, Expires = expires };
                (prefix ? t.Prefixes : t.Suffixes).Add(affix);
            }
        }

        private void ReadMeta(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            string[]? fields = ImportSupport.SplitFields(key, 3);
            if (fields is null || fields[1].Length == 0)
            {
                Skip($"{t.Label} '{key}': expected meta.<key>.<value>");
                return;
            }
            if (!value)
            {
                Skip($"{t.Label} '{key}': a negated meta entry cannot be represented");
                return;
            }
            foreach (ContextDoc? ctx in contexts)
                t.Meta.Add(new MetaDoc { Key = fields[1], Value = fields[2], Context = ctx, Expires = expires });
        }

        private void ReadDisplayName(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            if (t.IsUser)
            {
                Skip($"{t.Label} '{key}': a display name on a user is not supported");
                return;
            }
            string[]? fields = ImportSupport.SplitFields(key, 2);
            if (fields is null || fields[1].Length == 0)
            {
                Skip($"{t.Label} '{key}': no display name");
                return;
            }
            if (!value || expires is not null || contexts.Count != 1 || contexts[0] is not null)
            {
                Skip($"{t.Label} '{key}': only a permanent, context-free display name can be imported");
                return;
            }
            t.DisplayName = fields[1];
        }

        private void ReadRule(string key, bool value, long? expires, List<ContextDoc?> contexts, Target t)
        {
            string node = key;
            bool allowed = value;
            if (key.StartsWith('-'))
            {
                node = key[1..];
                allowed = false;
                Note($"{t.Label}: '{key}' is read as '{node}' = false");
            }
            if (node.Length == 0)
            {
                Skip($"{t.Label} '{key}': empty node");
                return;
            }
            foreach (ContextDoc? ctx in contexts)
            {
                if (t.Rules.Count >= ImportLimits.MaxRulesPerSubject)
                {
                    if (!t.RulesCapped)
                    {
                        t.RulesCapped = true;
                        Skip($"{t.Label}: more than {ImportLimits.MaxRulesPerSubject} rules; the rest are dropped");
                    }
                    return;
                }
                t.Rules.Add(new RuleDoc { Node = node, Value = allowed, Context = ctx, Expires = expires });
                _rules++;
            }
        }

        /// <summary>Expands a context object into one context pattern per combination of values (null = any). Returns the reason when it cannot be represented.</summary>
        private string? ExpandContext(JsonElement context, List<ContextDoc?> into)
        {
            if (context.ValueKind != JsonValueKind.Object) return "context is not an object";
            var variants = new List<ContextDoc> { new() };
            foreach (JsonProperty p in context.EnumerateObject())
            {
                List<string>? raw = ScalarValues(p.Value);
                if (raw is null || raw.Count == 0) return $"context '{p.Name}' has no usable value";
                string? problem = MapContext(p.Name, raw, out ContextField field, out List<string> values);
                if (problem is not null) return problem;
                if (field == ContextField.None) continue;

                var next = new List<ContextDoc>();
                foreach (ContextDoc variant in variants)
                    foreach (string value in values)
                    {
                        ContextDoc c = Copy(variant);
                        Apply(c, field, value);
                        next.Add(c);
                    }
                if (next.Count > ImportLimits.MaxContextVariants) return $"context expands to more than {ImportLimits.MaxContextVariants} combinations";
                variants = next;
            }
            foreach (ContextDoc variant in variants) into.Add(variant.IsAny ? null : variant);
            return null;
        }

        /// <summary>Maps one context key and its values to the store's fields. Field None means "any server/world/...": the key is not a constraint.</summary>
        private string? MapContext(string key, List<string> raw, out ContextField field, out List<string> values)
        {
            field = ContextField.None;
            values = new List<string>();
            if (key is not ("world" or "gamemode" or "server" or "dimension" or "dimension-type")) return $"unsupported context key '{key}'";
            if (raw.Any(v => v.Equals("global", StringComparison.OrdinalIgnoreCase))) return null;

            switch (key)
            {
                case "world":
                    field = ContextField.World;
                    foreach (string v in raw)
                    {
                        if (v.Length == 0) return "empty world name";
                        values.Add(v);
                    }
                    return null;
                case "gamemode":
                    field = ContextField.Gamemode;
                    foreach (string v in raw)
                    {
                        string mode = v.ToLowerInvariant();
                        if (mode is not ("survival" or "creative" or "adventure" or "spectator")) return $"unknown gamemode '{v}'";
                        values.Add(mode);
                    }
                    return null;
                case "server":
                    field = ContextField.Server;
                    foreach (string v in raw)
                    {
                        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0 || id > 255)
                            return $"server '{v}' is not a numeric server id (0-255)";
                        values.Add(id.ToString(CultureInfo.InvariantCulture));
                    }
                    return null;
                default:
                    field = ContextField.Dimension;
                    foreach (string v in raw)
                    {
                        string? dimension = DimensionName(v);
                        if (dimension is null) return $"unknown dimension '{v}'";
                        if (dimension != v) Remap($"dimension '{v}' -> '{dimension}'");
                        values.Add(dimension);
                    }
                    return null;
            }
        }
    }

    private static bool TryReadSeconds(JsonElement element, out long seconds)
    {
        if (element.TryGetInt64(out seconds)) return true;
        if (element.TryGetDouble(out double d) && d >= 0 && d < 9.0e15)
        {
            seconds = (long)d;
            return true;
        }
        seconds = 0;
        return false;
    }

    private static string? StringField(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The scalar values of a context entry: a string, a number, or an array of those. Null when the shape is not one of these.</summary>
    private static List<string>? ScalarValues(JsonElement element)
    {
        var list = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                list.Add(element.GetString() ?? "");
                return list;
            case JsonValueKind.Number:
                list.Add(element.GetRawText());
                return list;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Number) list.Add(item.GetRawText());
                    else return null;
                }
                return list;
            default:
                return null;
        }
    }

    /// <summary>The store's dimension name for a LuckPerms dimension value, or null when it is not one of the four.</summary>
    private static string? DimensionName(string raw)
    {
        string v = raw.ToLowerInvariant();
        if (v.StartsWith("minecraft:", StringComparison.Ordinal)) v = v["minecraft:".Length..];
        return v switch
        {
            "overworld" => "overworld",
            "the_nether" or "nether" => "the_nether",
            "the_end" or "end" => "the_end",
            "other" => "other",
            _ => null,
        };
    }

    private static ContextDoc Copy(ContextDoc c) => new()
    {
        World = c.World,
        Dimension = c.Dimension,
        Gamemode = c.Gamemode,
        Server = c.Server,
        Slot0 = c.Slot0,
        Slot1 = c.Slot1,
    };

    private static void Apply(ContextDoc c, ContextField field, string value)
    {
        switch (field)
        {
            case ContextField.World:
                c.World = value;
                break;
            case ContextField.Dimension:
                c.Dimension = value;
                break;
            case ContextField.Gamemode:
                c.Gamemode = value;
                break;
            case ContextField.Server:
                c.Server = int.Parse(value, CultureInfo.InvariantCulture);
                break;
        }
    }
}
