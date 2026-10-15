using Charter.Model;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>What <see cref="StoreFile.Load"/> returns: the document, or the error text (no document then).</summary>
public sealed record StoreLoad(StoreDoc? Doc, string? Error, IReadOnlyList<string> Notes);

/// <summary>store.json: loading with validation, the atomic save (one backup generation), the deep copy every change works on, and the checks that
/// every change goes through. The file is canonical; nothing here keeps a second copy.</summary>
public static class StoreFile
{
    public const string FileName = "store.json";
    public const string TempName = "store.json.tmp";
    public const string BackupName = "store.json.bak";
    public const string DefaultGroup = "default";

    public const int MaxUsers = 10000;
    public const int MaxGroups = 1000;
    public const int MaxTracks = 256;
    public const int MaxTrackGroups = 64;
    public const int MaxRulesPerSubject = 4096;
    public const int MaxMetaPerSubject = 256;
    public const int MaxAffixesPerKind = 64;
    public const int MaxMembershipsPerSubject = 64;
    public const int MaxSlots = 2;
    public const int MaxSlotValues = 15;
    public const int MaxNodeBytes = 256;
    public const int MaxNodeSegments = 16;
    public const int MaxServerId = 254;
    public const int MaxUserNameLength = 16;
    public const int MaxDisplayNameLength = 64;
    public const int MaxAffixTextLength = 256;
    public const int MaxMetaValueLength = 1024;
    public const int MaxWorldNameLength = 96;

    private static readonly string[] Dimensions = ["overworld", "the_nether", "the_end", "other"];
    private static readonly string[] GameModes = ["survival", "creative", "adventure", "spectator"];

    /// <summary>A document with only the default group (the first run).</summary>
    public static StoreDoc CreateEmpty()
    {
        var doc = new StoreDoc();
        doc.Groups[DefaultGroup] = new GroupDoc();
        return doc;
    }

    /// <summary>Reads <c>store.json</c> from the data folder. A missing file is created with the default group. A file that does not parse, is newer than
    /// this Charter, or fails validation gives an error text (with the line number of a syntax error) and no document.</summary>
    public static StoreLoad Load(string dataFolder)
    {
        string path = Path.Combine(dataFolder, FileName);
        var notes = new List<string>();
        if (!File.Exists(path))
        {
            var fresh = CreateEmpty();
            string? created = Save(dataFolder, fresh);
            if (created is not null) return new StoreLoad(null, created, notes);
            notes.Add($"created {path} with the default group");
            return new StoreLoad(fresh, null, notes);
        }

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new StoreLoad(null, $"{path}: {e.Message}", notes);
        }

        StoreDoc? parsed;
        try { parsed = JsonSerializer.Deserialize(text, StoreJson.Default.StoreDoc); }
        catch (JsonException e)
        {
            string line = e.LineNumber is { } l ? $" (line {l + 1})" : "";
            return new StoreLoad(null, $"{path}{line}: {e.Message}", notes);
        }
        if (parsed is null) return new StoreLoad(null, $"{path}: the file is empty", notes);
        if (parsed.SchemaVersion > StoreDoc.CurrentSchema)
            return new StoreLoad(null, $"{path}: schemaVersion {parsed.SchemaVersion} is newer than this Charter understands ({StoreDoc.CurrentSchema}); refusing to load it", notes);

        Normalize(parsed, notes);
        string? invalid = Validate(parsed);
        if (invalid is not null) return new StoreLoad(null, $"{path}: {invalid}", notes);
        return new StoreLoad(parsed, null, notes);
    }

    /// <summary>Fills what a file may leave out: the default group, empty lists, the schema version. Nothing a person wrote is changed.</summary>
    public static void Normalize(StoreDoc doc, List<string> notes)
    {
        if (doc.SchemaVersion < 1)
        {
            notes.Add($"store.json schemaVersion {doc.SchemaVersion} is older than {StoreDoc.CurrentSchema}: migrated");
            doc.SchemaVersion = StoreDoc.CurrentSchema;
        }
        doc.Groups ??= new Dictionary<string, GroupDoc>(StringComparer.Ordinal);
        doc.Users ??= new Dictionary<string, UserDoc>(StringComparer.Ordinal);
        doc.Tracks ??= new Dictionary<string, TrackDoc>(StringComparer.Ordinal);
        doc.Slots ??= [];
        if (!doc.Groups.ContainsKey(DefaultGroup))
        {
            doc.Groups[DefaultGroup] = new GroupDoc();
            notes.Add("store.json had no 'default' group: created it");
        }
        foreach (GroupDoc? g in doc.Groups.Values)
        {
            if (g is null) continue;
            g.Parents ??= [];
            g.Rules ??= [];
            g.Meta ??= [];
            g.Prefixes ??= [];
            g.Suffixes ??= [];
        }
        foreach (UserDoc? u in doc.Users.Values)
        {
            if (u is null) continue;
            u.Groups ??= [];
            u.Rules ??= [];
            u.Meta ??= [];
            u.Prefixes ??= [];
            u.Suffixes ??= [];
        }
        foreach (TrackDoc? t in doc.Tracks.Values)
        {
            if (t is not null) t.Groups ??= [];
        }
        foreach (ContextSlotDoc? s in doc.Slots)
        {
            if (s is not null) s.Values ??= [];
        }
    }

    /// <summary>Checks a whole document. Returns the first problem as a sentence with its JSON path, or null when the document is valid.</summary>
    public static string? Validate(StoreDoc doc)
    {
        if (doc.SchemaVersion > StoreDoc.CurrentSchema) return $"schemaVersion {doc.SchemaVersion} is newer than this Charter understands";
        if (doc.ServerId is < 0 or > MaxServerId) return $"serverId {doc.ServerId} must be 0 to {MaxServerId}";
        if (doc.Slots.Count > MaxSlots) return $"at most {MaxSlots} context slots are allowed";
        for (int i = 0; i < doc.Slots.Count; i++)
        {
            ContextSlotDoc? slot = doc.Slots[i];
            if (slot is null) return $"slots[{i}] is null";
            if (!IsValidName(slot.Key)) return $"slots[{i}].key '{slot.Key}' must be 1 to 32 characters of a-z, 0-9, '_' or '-'";
            if (slot.Values.Count > MaxSlotValues) return $"slots[{i}] has more than {MaxSlotValues} values";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in slot.Values)
            {
                if (!IsPlainToken(value, 32)) return $"slots[{i}] value '{value}' must be 1 to 32 printable characters without spaces";
                if (!seen.Add(value)) return $"slots[{i}] value '{value}' is listed twice";
            }
        }

        if (doc.Groups.Count > MaxGroups) return $"more than {MaxGroups} groups";
        if (!doc.Groups.ContainsKey(DefaultGroup)) return "the group 'default' is missing";
        foreach (var (name, group) in doc.Groups)
        {
            if (!IsValidName(name)) return $"group name '{name}' must be 1 to 32 characters of a-z, 0-9, '_' or '-'";
            if (group is null) return $"groups.{name} is null";
            string? problem = ValidateGroup(doc, name, group);
            if (problem is not null) return problem;
        }

        if (doc.Users.Count > MaxUsers) return $"more than {MaxUsers} users";
        foreach (var (key, user) in doc.Users)
        {
            if (!UuidText.TryParse(key, out Uuid uuid) || uuid.ToString() != key) return $"users key '{key}' must be a lower-case uuid with dashes";
            if (user is null) return $"users.{key} is null";
            string? problem = ValidateUser(doc, key, user);
            if (problem is not null) return problem;
        }

        if (doc.Tracks.Count > MaxTracks) return $"more than {MaxTracks} tracks";
        foreach (var (name, track) in doc.Tracks)
        {
            if (!IsValidName(name)) return $"track name '{name}' must be 1 to 32 characters of a-z, 0-9, '_' or '-'";
            if (track is null) return $"tracks.{name} is null";
            if (track.Groups.Count > MaxTrackGroups) return $"tracks.{name} has more than {MaxTrackGroups} groups";
            foreach (string group in track.Groups)
                if (!doc.Groups.ContainsKey(group)) return $"tracks.{name} lists '{group}', which is not a group";
        }
        return null;
    }

    /// <summary>Writes the document: a temporary file is flushed to disk, then it replaces <c>store.json</c> and the old file becomes
    /// <c>store.json.bak</c>. A failure leaves the old file in place.</summary>
    /// <returns>An error text, or null when the file was written.</returns>
    public static string? Save(string dataFolder, StoreDoc doc)
    {
        string store = Path.Combine(dataFolder, FileName);
        string temp = Path.Combine(dataFolder, TempName);
        string backup = Path.Combine(dataFolder, BackupName);
        try
        {
            Directory.CreateDirectory(dataFolder);
            string json = JsonSerializer.Serialize(doc, StoreJson.Default.StoreDoc);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(store)) File.Replace(temp, store, backup, ignoreMetadataErrors: true);
            else File.Move(temp, store, overwrite: false);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception) { /* best effort: the temporary file is harmless */ }
            return $"could not save {store}: {e.Message}";
        }
    }

    /// <summary>A deep copy (a round trip through the source-generated serializer).</summary>
    public static StoreDoc DeepCopy(StoreDoc doc)
    {
        string json = JsonSerializer.Serialize(doc, StoreJson.Default.StoreDoc);
        return JsonSerializer.Deserialize(json, StoreJson.Default.StoreDoc) ?? throw new InvalidOperationException("the store copy came back empty");
    }

    /// <summary>Group and track names: 1 to 32 characters of a-z, 0-9, '_' and '-'.</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > 32) return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
        return true;
    }

    /// <summary>A permission node: printable ASCII without spaces, at most 256 bytes and 16 dot-separated segments, no empty segment, '*' only as the
    /// whole last segment, no regex characters (D12). Returns the problem, or null.</summary>
    public static string? ValidateNode(string node)
    {
        if (node.Length == 0) return "the node is empty";
        if (Encoding.UTF8.GetByteCount(node) > MaxNodeBytes) return $"the node is longer than {MaxNodeBytes} bytes";
        foreach (char c in node)
        {
            if (c < '!' || c > '~') return $"the node '{node}' has a space or a character that is not printable ASCII";
            if ("\\^$[](){}|+?".Contains(c)) return $"the node '{node}' has a regex character ('{c}'); regex nodes are not supported";
        }
        string[] segments = node.Split('.');
        if (segments.Length > MaxNodeSegments) return $"the node '{node}' has more than {MaxNodeSegments} segments";
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0) return $"the node '{node}' has an empty segment";
            if (segment.Contains('*'))
            {
                if (segment != "*") return $"the node '{node}': '*' must be a whole segment";
                if (i != segments.Length - 1) return $"the node '{node}': '*' is only allowed as the last segment";
            }
        }
        return null;
    }

    private static string? ValidateGroup(StoreDoc doc, string name, GroupDoc group)
    {
        if (group.DisplayName is { Length: > MaxDisplayNameLength }) return $"groups.{name}.displayName is longer than {MaxDisplayNameLength} characters";
        string? problem = ValidateMemberships(doc, $"groups.{name}.parents", group.Parents);
        problem ??= ValidateRules(doc, $"groups.{name}.rules", group.Rules);
        problem ??= ValidateMeta(doc, $"groups.{name}.meta", group.Meta);
        problem ??= ValidateAffixes(doc, $"groups.{name}.prefixes", group.Prefixes);
        problem ??= ValidateAffixes(doc, $"groups.{name}.suffixes", group.Suffixes);
        return problem;
    }

    private static string? ValidateUser(StoreDoc doc, string key, UserDoc user)
    {
        if (user.Name is { Length: > MaxUserNameLength }) return $"users.{key}.name is longer than {MaxUserNameLength} characters";
        string? problem = ValidateMemberships(doc, $"users.{key}.groups", user.Groups);
        problem ??= ValidateRules(doc, $"users.{key}.rules", user.Rules);
        problem ??= ValidateMeta(doc, $"users.{key}.meta", user.Meta);
        problem ??= ValidateAffixes(doc, $"users.{key}.prefixes", user.Prefixes);
        problem ??= ValidateAffixes(doc, $"users.{key}.suffixes", user.Suffixes);
        return problem;
    }

    private static string? ValidateMemberships(StoreDoc doc, string path, List<Membership> list)
    {
        if (list.Count > MaxMembershipsPerSubject) return $"{path} has more than {MaxMembershipsPerSubject} entries";
        for (int i = 0; i < list.Count; i++)
        {
            Membership? m = list[i];
            if (m is null) return $"{path}[{i}] is null";
            if (!doc.Groups.ContainsKey(m.Group)) return $"{path}[{i}].group '{m.Group}' is not a group";
            string? problem = ValidateContext(doc, $"{path}[{i}].context", m.Context) ?? ValidateExpiry($"{path}[{i}]", m.Expires);
            if (problem is not null) return problem;
        }
        return null;
    }

    private static string? ValidateRules(StoreDoc doc, string path, List<RuleDoc> rules)
    {
        if (rules.Count > MaxRulesPerSubject) return $"{path} has more than {MaxRulesPerSubject} rules";
        for (int i = 0; i < rules.Count; i++)
        {
            RuleDoc? r = rules[i];
            if (r is null) return $"{path}[{i}] is null";
            string? problem = ValidateNode(r.Node) is { } nodeProblem ? $"{path}[{i}].node: {nodeProblem}" : null;
            problem ??= ValidateContext(doc, $"{path}[{i}].context", r.Context) ?? ValidateExpiry($"{path}[{i}]", r.Expires);
            if (problem is not null) return problem;
        }
        return null;
    }

    private static string? ValidateMeta(StoreDoc doc, string path, List<MetaDoc> meta)
    {
        if (meta.Count > MaxMetaPerSubject) return $"{path} has more than {MaxMetaPerSubject} entries";
        for (int i = 0; i < meta.Count; i++)
        {
            MetaDoc? m = meta[i];
            if (m is null) return $"{path}[{i}] is null";
            if (!IsPlainToken(m.Key, 64)) return $"{path}[{i}].key must be 1 to 64 printable characters without spaces";
            if (m.Value.Length > MaxMetaValueLength) return $"{path}[{i}].value is longer than {MaxMetaValueLength} characters";
            string? problem = ValidateContext(doc, $"{path}[{i}].context", m.Context) ?? ValidateExpiry($"{path}[{i}]", m.Expires);
            if (problem is not null) return problem;
        }
        return null;
    }

    private static string? ValidateAffixes(StoreDoc doc, string path, List<AffixDoc> affixes)
    {
        if (affixes.Count > MaxAffixesPerKind) return $"{path} has more than {MaxAffixesPerKind} entries";
        for (int i = 0; i < affixes.Count; i++)
        {
            AffixDoc? a = affixes[i];
            if (a is null) return $"{path}[{i}] is null";
            if (a.Text.Length == 0 || a.Text.Length > MaxAffixTextLength) return $"{path}[{i}].text must be 1 to {MaxAffixTextLength} characters";
            if (a.Text.Any(ch => ch < ' ')) return $"{path}[{i}].text has a control character";
            string? problem = ValidateContext(doc, $"{path}[{i}].context", a.Context) ?? ValidateExpiry($"{path}[{i}]", a.Expires);
            if (problem is not null) return problem;
        }
        return null;
    }

    private static string? ValidateContext(StoreDoc doc, string path, ContextDoc? c)
    {
        if (c is null) return null;
        if (c.World is { } world && (world.Length == 0 || world.Length > MaxWorldNameLength || world.Any(ch => ch <= ' ' || ch > '~')))
            return $"{path}.world must be a world name of 1 to {MaxWorldNameLength} printable characters without spaces";
        if (c.Dimension is { } dimension && !Dimensions.Contains(dimension))
            return $"{path}.dimension '{dimension}' must be one of overworld, the_nether, the_end, other";
        if (c.Gamemode is { } gamemode && !GameModes.Contains(gamemode))
            return $"{path}.gamemode '{gamemode}' must be one of survival, creative, adventure, spectator";
        if (c.Server is { } server && (server < 0 || server > MaxServerId)) return $"{path}.server must be 0 to {MaxServerId}";
        if (c.Slot0 is { } slot0 && (doc.Slots.Count < 1 || !doc.Slots[0].Values.Contains(slot0)))
            return $"{path}.slot0 '{slot0}' is not a value of slots[0]";
        if (c.Slot1 is { } slot1 && (doc.Slots.Count < 2 || !doc.Slots[1].Values.Contains(slot1)))
            return $"{path}.slot1 '{slot1}' is not a value of slots[1]";
        return null;
    }

    private static string? ValidateExpiry(string path, long? expires)
        => expires is { } at && at <= 0 ? $"{path}.expires must be a unix time in seconds" : null;

    private static bool IsPlainToken(string text, int max)
    {
        if (text.Length == 0 || text.Length > max) return false;
        foreach (char c in text)
            if (c < '!' || c > '~') return false;
        return true;
    }
}

/// <summary>Serializer context for the parts of the document that change detection compares one by one (no reflection: source generated).</summary>
[JsonSerializable(typeof(GroupDoc))]
[JsonSerializable(typeof(UserDoc))]
[JsonSerializable(typeof(TrackDoc))]
internal sealed partial class StoreCompareJson : JsonSerializerContext;
