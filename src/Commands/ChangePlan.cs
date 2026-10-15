using System.Text.Json;
using Mc.Scripting.Commands;
using Charter.Core;
using Charter.Model;
using Mc.Scripting;
using Mc.Scripting.Permissions;

namespace Charter.Commands;

/// <summary>Whether a write leaf of the tree applies its change or only previews it (<c>/charter preview ...</c>).</summary>
internal enum Mode
{
    Apply,
    Preview,
}

/// <summary>One change, parsed and ready to run. Built once by a command leaf and run by CharterTree.Execute (Apply or dry run).</summary>
/// <param name="Action">The audit action, e.g. "user.set" or "group.parent.add".</param>
/// <param name="Detail">The audit detail text (the subject's name first, so audit can filter by it).</param>
/// <param name="Summary">The reply text. A function so an edit can report what it decided (track promote: from and to).</param>
/// <param name="Edit">The edit on a copy of the document; returns an error text to abort, null to commit.</param>
/// <param name="User">The one user the change is about, or null.</param>
/// <param name="Groups">Groups whose rules or memberships the change touches (their ancestors are included by the preview).</param>
/// <param name="Node">The permission node pattern the change sets or unsets, or null.</param>
internal sealed record ChangePlan(string Action, string Detail, Func<string> Summary, Func<StoreDoc, string?> Edit,
    Uuid? User, IReadOnlyList<string> Groups, string? Node);

/// <summary>Runs a plan for real (through Core) or as a dry run: a copy of the document gets the edit, and every online player's effective
/// answer is compared before and after for the nodes the change touches. Nothing is written in a dry run.</summary>
internal static class ChangePreview
{
    /// <summary>Lines printed by a dry run or an import diff; the rest is counted.</summary>
    public const int MaxLines = 50;

    /// <summary>Concrete nodes checked at most (a "*" change over many registered nodes is capped).</summary>
    public const int MaxNodes = 1000;

    /// <summary>A player or user in a diff: uuid, the name to show, and the query context the check uses.</summary>
    public sealed record Subject(Uuid Id, string Name, QueryContext Context);

    public static IReadOnlyList<Subject> OnlinePlayers(ICharterCore core)
    {
        var list = new List<Subject>();
        foreach (Player player in Server.Players)
        {
            list.Add(new Subject(player.Uuid, player.Name, core.ContextOf(player.Uuid)));
        }
        return list;
    }

    /// <summary>Expands wildcard patterns over the registered nodes; exact nodes pass through. Sorted, distinct, capped at <see cref="MaxNodes"/>.</summary>
    public static IReadOnlyList<string> ExpandNodes(IEnumerable<string> patterns)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        IReadOnlyList<Permission>? registered = null;
        foreach (string pattern in patterns)
        {
            if (!pattern.Contains('*'))
            {
                result.Add(pattern);
                continue;
            }
            registered ??= PermissionManager.Registered;
            foreach (Permission permission in registered)
            {
                if (Matches(pattern, permission.Node)) result.Add(permission.Node);
            }
        }
        var list = result.ToList();
        if (list.Count > MaxNodes) list.RemoveRange(MaxNodes, list.Count - MaxNodes);
        return list;
    }

    /// <summary>A dry run of <paramref name="plan"/> for <paramref name="subjects"/>: prints the change and the gains and losses. Throws a
    /// <see cref="CommandFailure"/> when the edit itself would be refused.</summary>
    public static void Run(ICharterCore core, ChangePlan plan, IReadOnlyList<Subject> subjects, Action<string> say)
    {
        StoreDoc before = core.Current;
        StoreDoc after = CloneDoc(before);
        string? error = plan.Edit(after);
        if (error is not null) throw new CommandFailure(error);

        var patterns = new List<string>();
        if (plan.Node is { } node)
        {
            patterns.Add(node);
        }
        else
        {
            foreach (string group in DocEdits.Closure(before, plan.Groups)) AddRuleNodes(before, group, patterns);
            foreach (string group in DocEdits.Closure(after, plan.Groups)) AddRuleNodes(after, group, patterns);
        }
        IReadOnlyList<string> nodes = ExpandNodes(patterns);
        say($"Dry run, nothing written: {plan.Summary()}");
        Diff(core, before, after, subjects.Select(s => (s, nodes)), say);
    }

    /// <summary>The diff of two whole documents (import dry run): every user in either document, the nodes their groups and rules mention.</summary>
    public static void Documents(ICharterCore core, StoreDoc before, StoreDoc after, Action<string> say)
    {
        var keys = new SortedSet<string>(before.Users.Keys, StringComparer.Ordinal);
        keys.UnionWith(after.Users.Keys);
        var subjects = new List<(Subject Who, IReadOnlyList<string> Nodes)>();
        foreach (string key in keys)
        {
            if (!UuidText.TryParse(key, out Uuid id)) continue;
            string name = after.Users.TryGetValue(key, out UserDoc? a) && a.Name is { } an ? an
                : before.Users.TryGetValue(key, out UserDoc? b) && b.Name is { } bn ? bn : key;
            IReadOnlyList<string> nodes = UserNodes(before, after, key);
            if (nodes.Count == 0) continue;
            subjects.Add((new Subject(id, name, core.ContextOf(id)), nodes));
        }
        Diff(core, before, after, subjects, say);
    }

    /// <summary>A deep copy of the document (through its JSON form, the way the store is written).</summary>
    public static StoreDoc CloneDoc(StoreDoc doc)
    {
        string json = JsonSerializer.Serialize(doc, StoreJson.Default.StoreDoc);
        return JsonSerializer.Deserialize(json, StoreJson.Default.StoreDoc) ?? new StoreDoc();
    }

    private static void Diff(ICharterCore core, StoreDoc before, StoreDoc after,
        IEnumerable<(Subject Who, IReadOnlyList<string> Nodes)> work, Action<string> say)
    {
        var lines = new List<string>();
        int gains = 0;
        int losses = 0;
        foreach (var (who, nodes) in work)
        {
            foreach (string node in nodes)
            {
                bool was = core.Has(who.Id, node, who.Context, before);
                bool now = core.Has(who.Id, node, who.Context, after);
                if (was == now) continue;
                if (now) gains++;
                else losses++;
                lines.Add($"  {(now ? "+" : "-")} {who.Name}: {node} {Word(was)} -> {Word(now)}");
            }
        }
        if (lines.Count == 0)
        {
            say("No effect: no checked player's answer changes.");
            return;
        }
        foreach (string line in lines.Take(MaxLines)) say(line);
        if (lines.Count > MaxLines) say($"  ... {lines.Count - MaxLines} more");
        say($"{gains} gained, {losses} lost.");
    }

    private static string Word(bool value) => value ? "true" : "false";

    private static IReadOnlyList<string> UserNodes(StoreDoc before, StoreDoc after, string key)
    {
        var patterns = new List<string>();
        foreach (StoreDoc doc in new[] { before, after })
        {
            if (!doc.Users.TryGetValue(key, out UserDoc? user)) continue;
            patterns.AddRange(user.Rules.Select(r => r.Node));
            IEnumerable<string> groups = user.Groups.Select(m => m.Group).Append("default");
            foreach (string group in DocEdits.Closure(doc, groups)) AddRuleNodes(doc, group, patterns);
        }
        return ExpandNodes(patterns);
    }

    private static void AddRuleNodes(StoreDoc doc, string group, List<string> patterns)
    {
        if (doc.Groups.TryGetValue(group, out GroupDoc? groupDoc)) patterns.AddRange(groupDoc.Rules.Select(r => r.Node));
    }

    private static bool Matches(string pattern, string node)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*", StringComparison.Ordinal)) return node.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return pattern == node;
    }
}
