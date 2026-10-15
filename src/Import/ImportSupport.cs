using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Charter.Core;
using Charter.Model;
using Mc.Scripting;

namespace Charter.Import;

/// <summary>Helpers shared by the importers: deep copy of the store, bounded reads, key escapes, names, memberships.</summary>
internal static class ImportSupport
{
    public const string DefaultGroup = "default";

    public static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A deep copy through the store's own JSON context (no reflection).</summary>
    public static StoreDoc DeepCopy(StoreDoc doc)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(doc, StoreJson.Default.StoreDoc);
        return JsonSerializer.Deserialize(json, StoreJson.Default.StoreDoc) ?? new StoreDoc();
    }

    /// <summary>Reads a whole file; refused (InvalidDataException) when it is larger than <paramref name="maxBytes"/>.</summary>
    public static byte[] ReadBounded(string path, long maxBytes)
    {
        using FileStream file = File.OpenRead(path);
        using var limited = new LimitedStream(file, maxBytes, path);
        using var buffer = new MemoryStream();
        limited.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static JsonDocument ParseOrThrow(byte[] bytes, string path)
    {
        try
        {
            return JsonDocument.Parse(bytes, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: not valid JSON ({e.Message})", e);
        }
    }

    public static void EnsureDefault(StoreDoc doc)
    {
        if (!doc.Groups.ContainsKey(DefaultGroup)) doc.Groups[DefaultGroup] = new GroupDoc();
    }

    public static string LowerName(string name) => name.ToLowerInvariant();

    /// <summary>The store's uuid text (lower-case, dashed) for any accepted spelling.</summary>
    public static bool TryNormalizeUuid(string text, out string normalized)
    {
        normalized = "";
        if (!UuidText.TryParse(text, out Uuid uuid)) return false;
        normalized = UuidText.Format(uuid);
        return true;
    }

    /// <summary>Index of the first '.' at or after <paramref name="start"/> that is not escaped by a backslash; -1 when there is none.</summary>
    public static int IndexOfSeparator(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '.') return i;
        }
        return -1;
    }

    /// <summary>Splits a key into <paramref name="count"/> unescaped fields; the last field takes the rest of the text. Null when there are too few fields.</summary>
    public static string[]? SplitFields(string key, int count)
    {
        var fields = new string[count];
        int pos = 0;
        for (int i = 0; i < count - 1; i++)
        {
            int dot = IndexOfSeparator(key, pos);
            if (dot < 0) return null;
            fields[i] = Unescape(key[pos..dot]);
            pos = dot + 1;
        }
        fields[count - 1] = Unescape(key[pos..]);
        return fields;
    }

    /// <summary>Removes escapes: a backslash makes the next character literal (LuckPerms escapes dots inside meta and affix text).</summary>
    public static string Unescape(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length) sb.Append(raw[++i]);
            else sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    public static string ContextKey(ContextDoc? c)
        => c is null ? "" : $"{c.World}|{c.Dimension}|{c.Gamemode}|{c.Server}|{c.Slot0}|{c.Slot1}";

    public static void AddMembership(List<Membership> list, HashSet<string> keys, string group, ContextDoc? ctx, long? expires)
    {
        if (keys.Add($"{group}|{ContextKey(ctx)}|{expires}")) list.Add(new Membership { Group = group, Context = ctx, Expires = expires });
    }

    /// <summary>The non-empty strings of a JSON array; other entries are reported and dropped.</summary>
    public static List<string> StringList(JsonElement array, ImportReport report, string where)
    {
        var result = new List<string>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            report.AddSkipped($"{where}: not an array");
            return result;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(text)) report.AddSkipped($"{where}: an entry is not a non-empty string");
            else result.Add(text);
        }
        return result;
    }

    /// <summary>Keeps a key the importer does not know, cloned out of the parsed document, and reports it.</summary>
    public static void KeepUnknown(ref Dictionary<string, JsonElement>? extra, JsonProperty property, ImportReport report, string where)
    {
        extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        extra[property.Name] = property.Value.Clone();
        report.AddNote($"{where}: unknown key '{property.Name}' kept under extra");
    }

    /// <summary>Reports memberships whose group is not in the document (they have no effect until it exists).</summary>
    public static void ReportDanglingMemberships(StoreDoc doc, ImportReport report)
    {
        foreach (KeyValuePair<string, GroupDoc> entry in doc.Groups)
            foreach (Membership parent in entry.Value.Parents)
                if (!doc.Groups.ContainsKey(parent.Group))
                    report.AddNote($"group '{entry.Key}' inherits '{parent.Group}', which is not in the store (no effect until it exists)");
        foreach (KeyValuePair<string, UserDoc> entry in doc.Users)
            foreach (Membership membership in entry.Value.Groups)
                if (!doc.Groups.ContainsKey(membership.Group))
                    report.AddNote($"user '{entry.Key}' is in '{membership.Group}', which is not in the store (no effect until it exists)");
    }
}
