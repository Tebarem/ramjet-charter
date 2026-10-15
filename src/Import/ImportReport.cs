using Mc.Scripting;
using Charter.Model;

namespace Charter.Import;

/// <summary>Limits applied by the importers. Checked while reading, before anything is handed back.</summary>
public static class ImportLimits
{
    /// <summary>Ramjet permissions.json: refused above this size.</summary>
    public const long MaxRamjetFileBytes = 16L * 1024 * 1024;

    /// <summary>LuckPerms export: refused when the uncompressed JSON is larger than this.</summary>
    public const long MaxLuckPermsJsonBytes = 64L * 1024 * 1024;

    /// <summary>Rules of one group or user (the engine's scripting.perms.max_rules_per_subject default). Extra rules are dropped and reported.</summary>
    public const int MaxRulesPerSubject = 4096;

    /// <summary>Groups in one import.</summary>
    public const int MaxGroups = 1024;

    /// <summary>Users in one import.</summary>
    public const int MaxUsers = 50_000;

    /// <summary>Context combinations one node may expand to (several values for several keys multiply).</summary>
    public const int MaxContextVariants = 64;

    /// <summary>Lines kept in each of Skipped, Remapped and Notes; the rest are counted only.</summary>
    public const int MaxListedLines = 5000;
}

/// <summary>What an import produced: the merged document plus everything skipped, remapped or noted. The input document is never modified.</summary>
public sealed class ImportReport
{
    /// <summary>The merged document: a deep copy of the current store with the imported groups, users and tracks applied.</summary>
    public StoreDoc Result { get; set; } = new();

    /// <summary>Items that were not imported, each with its reason.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Names or ids that were changed on the way in (lower-cased groups, dashed uuids, mapped dimensions).</summary>
    public List<string> Remapped { get; } = [];

    /// <summary>Replacements, unknown keys kept under extra, assumptions made, and counts.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Every skipped item, including the ones not listed in <see cref="Skipped"/>.</summary>
    public int SkippedCount { get; private set; }

    /// <summary>Every remapped item, including the ones not listed in <see cref="Remapped"/>.</summary>
    public int RemappedCount { get; private set; }

    /// <summary>Every note, including the ones not listed in <see cref="Notes"/>.</summary>
    public int NotesCount { get; private set; }

    internal void AddSkipped(string text)
    {
        SkippedCount++;
        AddListed(Skipped, text, SkippedCount);
    }

    internal void AddRemapped(string text)
    {
        RemappedCount++;
        AddListed(Remapped, text, RemappedCount);
    }

    internal void AddNote(string text)
    {
        NotesCount++;
        AddListed(Notes, text, NotesCount);
    }

    private static void AddListed(List<string> list, string text, int count)
    {
        if (count <= ImportLimits.MaxListedLines) list.Add(text);
        else if (count == ImportLimits.MaxListedLines + 1) list.Add("... further entries are counted but not listed");
    }
}
