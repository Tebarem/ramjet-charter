using Charter.Model;
using System.Collections.Generic;
using System.Linq;
using System;
using Mc.Scripting.Permissions;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>A context pattern: a null position means any value. World names compare normalised (a name without ':' is in minecraft:).</summary>
internal readonly record struct CtxPattern(string? World, string? Dimension, string? Gamemode, int? Server, string? Slot0, string? Slot1)
{
    public static readonly CtxPattern Any = default;

    public static CtxPattern From(ContextDoc? c) => c is null
        ? Any
        : new CtxPattern(c.World is null ? null : Resolver.NormWorld(c.World), Lower(c.Dimension), Lower(c.Gamemode), c.Server, c.Slot0, c.Slot1);

    /// <summary>How many positions are fixed (more = more specific).</summary>
    public int Specificity => (World is null ? 0 : 1) + (Dimension is null ? 0 : 1) + (Gamemode is null ? 0 : 1)
        + (Server is null ? 0 : 1) + (Slot0 is null ? 0 : 1) + (Slot1 is null ? 0 : 1);

    /// <summary>Whether a query matches: every fixed position must equal the query's value (an unknown query value matches only "any").</summary>
    public bool Matches(QueryContext q)
    {
        if (World is not null && (q.World is null || Resolver.NormWorld(q.World) != World)) return false;
        if (Dimension is not null && (q.Dimension is null || Lower(q.Dimension) != Dimension)) return false;
        if (Gamemode is not null && (q.Gamemode is null || Lower(q.Gamemode) != Gamemode)) return false;
        if (Server is not null && q.Server != Server) return false;
        if (Slot0 is not null && q.Slot0 != Slot0) return false;
        if (Slot1 is not null && q.Slot1 != Slot1) return false;
        return true;
    }

    /// <summary>The conjunction of two patterns (a context path through memberships, or a rule inside one): false when they contradict each other.</summary>
    public static bool TryIntersect(CtxPattern a, CtxPattern b, out CtxPattern result)
    {
        result = Any;
        if (!JoinS(a.World, b.World, out string? w)) return false;
        if (!JoinS(a.Dimension, b.Dimension, out string? d)) return false;
        if (!JoinS(a.Gamemode, b.Gamemode, out string? g)) return false;
        if (!JoinI(a.Server, b.Server, out int? s)) return false;
        if (!JoinS(a.Slot0, b.Slot0, out string? s0)) return false;
        if (!JoinS(a.Slot1, b.Slot1, out string? s1)) return false;
        result = new CtxPattern(w, d, g, s, s0, s1);
        return true;
    }

    private static bool JoinS(string? a, string? b, out string? r)
    {
        r = a ?? b;
        return a is null || b is null || a == b;
    }

    private static bool JoinI(int? a, int? b, out int? r)
    {
        r = a ?? b;
        return a is null || b is null || a == b;
    }

    private static string? Lower(string? s) => s?.ToLowerInvariant();
}

/// <summary>One group of a user's plan: its layer (1-based position in the chain), and the context paths it is active under (empty = never active).</summary>
internal sealed record PlanGroup(string Name, GroupDoc Doc, int Layer, IReadOnlyList<CtxPattern> Patterns);

/// <summary>A user's plan: the user (null when the store has no entry: only the default group applies), the groups in layer order, and notes for the log.</summary>
internal sealed record Plan(UserDoc? User, IReadOnlyList<PlanGroup> Groups, List<string> Notes);

/// <summary>One rule, meta value or affix of a plan, with the layer it lives in and the context it is active under.</summary>
internal readonly record struct Entry<T>(int Layer, CtxPattern Ctx, string Source, T Item) where T : class;

/// <summary>The pure resolver (D3). Inputs: the document, the user, the node or key, the query context and the current time (unix seconds). No host calls.
/// Layers: 0 = the user's own entries, then each group of the user's chain in order (own groups by weight, parents after their child, "default" and its
/// parents last). The group order ignores contexts, so the native table and this resolver use the same layers; a membership context only decides whether
/// the group's entries apply, through their context path.</summary>
public static class Resolver
{
    /// <summary>The highest layer number the table holds (layers are 0 to 1023).</summary>
    public const int MaxLayer = 1023;

    /// <summary>Context paths kept per group; more would only come from a pathological membership graph.</summary>
    public const int MaxPatternsPerGroup = 32;

    /// <summary>The D3 answer for a node: the first layer with a matching rule decides; inside it the most specific context, then the most specific node,
    /// then a denial over a grant. <see cref="PermissionValue.Unset"/> when no rule matches (the caller applies the node default).</summary>
    public static Resolution Resolve(StoreDoc doc, Uuid user, string node, QueryContext ctx, long now)
    {
        Plan plan = BuildPlan(doc, user, now);
        Entry<RuleDoc>? best = null;
        int bestSpec = 0, bestNode = 0;
        foreach (Entry<RuleDoc> e in Collect<RuleDoc>(plan, now, u => u.Rules, g => g.Rules, r => r.Context, r => r.Expires))
        {
            if (!NodeMatches(e.Item.Node, node) || !e.Ctx.Matches(ctx)) continue;
            int spec = e.Ctx.Specificity;
            int nodeSpec = NodeSpecificity(e.Item.Node);
            if (best is { } b && !RuleOutranks(e, spec, nodeSpec, b, bestSpec, bestNode)) continue;
            best = e;
            bestSpec = spec;
            bestNode = nodeSpec;
        }
        if (best is not { } winner) return new Resolution(PermissionValue.Unset, -1, null, null);
        return new Resolution(winner.Item.Value ? PermissionValue.True : PermissionValue.False, winner.Layer, winner.Source, winner.Item);
    }

    /// <summary>The user's groups that apply in the context, in layer order. "default" is always last among them. Expired memberships are skipped.</summary>
    public static IReadOnlyList<string> EffectiveGroups(StoreDoc doc, Uuid user, QueryContext ctx, long now)
    {
        Plan plan = BuildPlan(doc, user, now);
        var names = new List<string>();
        foreach (PlanGroup g in plan.Groups)
            if (g.Name == StoreFile.DefaultGroup || g.Patterns.Any(p => p.Matches(ctx))) names.Add(g.Name);
        return names;
    }

    /// <summary>The plan of a user at a time: the layer order and the context paths of every group in it.</summary>
    internal static Plan BuildPlan(StoreDoc doc, Uuid user, long now)
    {
        var notes = new List<string>();
        doc.Users.TryGetValue(user.ToString(), out UserDoc? ud);
        Dictionary<string, GroupDoc> groups = doc.Groups;

        bool Live(long? expires) => expires is not { } at || at > now;
        bool Usable(Membership m) => m.Group is not null && groups.ContainsKey(m.Group) && m.Group != StoreFile.DefaultGroup && Live(m.Expires);

        // 1. the order: a depth-first walk over the live memberships, contexts ignored
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name)
        {
            if (!seen.Add(name)) return;
            order.Add(name);
            if (!groups.TryGetValue(name, out GroupDoc? gd)) return;
            foreach (string parent in ParentsOf(gd, now, groups)) Visit(parent);
        }
        if (ud is not null)
        {
            var own = ud.Groups.Where(m => m is not null && Usable(m))
                .Select(m => m.Group)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(n => groups[n].Weight)
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            foreach (string name in own) Visit(name);
        }
        if (groups.ContainsKey(StoreFile.DefaultGroup)) Visit(StoreFile.DefaultGroup);

        // 2. the context paths: a group is active under the intersection of the membership contexts along a path from the user or the default group
        var patterns = new Dictionary<string, List<CtxPattern>>(StringComparer.Ordinal);
        var work = new Queue<string>();
        void Add(string name, CtxPattern p)
        {
            if (!patterns.TryGetValue(name, out List<CtxPattern>? list)) patterns[name] = list = [];
            if (list.Contains(p)) return;
            if (list.Count >= MaxPatternsPerGroup)
            {
                notes.Add($"group '{name}' has more than {MaxPatternsPerGroup} context paths for one user; the extra paths are ignored");
                return;
            }
            list.Add(p);
            work.Enqueue(name);
        }
        if (ud is not null)
            foreach (Membership m in ud.Groups)
                if (m is not null && Usable(m)) Add(m.Group, CtxPattern.From(m.Context));
        if (groups.ContainsKey(StoreFile.DefaultGroup)) Add(StoreFile.DefaultGroup, CtxPattern.Any);
        while (work.Count > 0)
        {
            string name = work.Dequeue();
            if (!groups.TryGetValue(name, out GroupDoc? gd)) continue;
            foreach (Membership parent in gd.Parents)
            {
                if (parent is null || parent.Group is null || !groups.ContainsKey(parent.Group) || parent.Group == StoreFile.DefaultGroup || !Live(parent.Expires)) continue;
                CtxPattern edge = CtxPattern.From(parent.Context);
                foreach (CtxPattern c in patterns[name].ToList())
                    if (CtxPattern.TryIntersect(c, edge, out CtxPattern path)) Add(parent.Group, path);
            }
        }

        // 3. the layers: position in the order, capped
        if (order.Count > MaxLayer) notes.Add($"a player's group chain has more than {MaxLayer} groups; the deepest share layer {MaxLayer}");
        var planned = new List<PlanGroup>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            string name = order[i];
            if (!groups.TryGetValue(name, out GroupDoc? gd)) continue;
            IReadOnlyList<CtxPattern> ps = patterns.TryGetValue(name, out List<CtxPattern>? found) ? found : new List<CtxPattern>();
            planned.Add(new PlanGroup(name, gd, Math.Min(i + 1, MaxLayer), ps));
        }
        return new Plan(ud, planned, notes);
    }

    /// <summary>Every entry of a plan that is live at <paramref name="now"/>, with its layer and the context it is active under. The user's own entries are
    /// layer 0, source "user".</summary>
    internal static List<Entry<T>> Collect<T>(Plan plan, long now, Func<UserDoc, IEnumerable<T>> fromUser, Func<GroupDoc, IEnumerable<T>> fromGroup,
        Func<T, ContextDoc?> contextOf, Func<T, long?> expiresOf) where T : class
    {
        var result = new List<Entry<T>>();
        bool Live(long? expires) => expires is not { } at || at > now;
        if (plan.User is { } user)
        {
            foreach (T item in fromUser(user))
            {
                if (item is null || !Live(expiresOf(item))) continue;
                result.Add(new Entry<T>(0, CtxPattern.From(contextOf(item)), "user", item));
            }
        }
        foreach (PlanGroup group in plan.Groups)
        {
            if (group.Patterns.Count == 0) continue;
            foreach (T item in fromGroup(group.Doc))
            {
                if (item is null || !Live(expiresOf(item))) continue;
                CtxPattern entry = CtxPattern.From(contextOf(item));
                foreach (CtxPattern path in group.Patterns)
                    if (CtxPattern.TryIntersect(entry, path, out CtxPattern active))
                        result.Add(new Entry<T>(group.Layer, active, group.Name, item));
            }
        }
        return result;
    }

    /// <summary>Whether a node pattern covers a node: exact, "a.b.*" (any depth below a.b), or "*".</summary>
    internal static bool NodeMatches(string pattern, string node)
    {
        if (pattern == node || pattern == "*") return true;
        return pattern.EndsWith(".*", StringComparison.Ordinal) && node.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal);
    }

    /// <summary>Exact beats the longest wildcard, which beats "*".</summary>
    internal static int NodeSpecificity(string pattern)
    {
        if (pattern == "*") return 0;
        return pattern.EndsWith(".*", StringComparison.Ordinal) ? pattern.Length : int.MaxValue;
    }

    /// <summary>The native name of a world: a name without a namespace is in "minecraft".</summary>
    internal static string NormWorld(string name)
        => name.Contains(':') ? name.ToLowerInvariant() : "minecraft:" + name.ToLowerInvariant();

    /// <summary>Whether a rule beats the current best inside the D3 order: lower layer, then more specific context, then more specific node, then a denial.</summary>
    private static bool RuleOutranks(Entry<RuleDoc> e, int spec, int nodeSpec, Entry<RuleDoc> best, int bestSpec, int bestNode)
    {
        if (e.Layer != best.Layer) return e.Layer < best.Layer;
        if (spec != bestSpec) return spec > bestSpec;
        if (nodeSpec != bestNode) return nodeSpec > bestNode;
        return !e.Item.Value && best.Item.Value;
    }

    /// <summary>Parents of a group that take part in the order: live, known, and not "default" (which is always placed last).</summary>
    private static IEnumerable<string> ParentsOf(GroupDoc group, long now, Dictionary<string, GroupDoc> groups)
        => group.Parents
            .Where(p => p is not null && p.Group is not null && groups.ContainsKey(p.Group) && p.Group != StoreFile.DefaultGroup
                && (p.Expires is not { } at || at > now))
            .Select(p => p.Group)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(n => groups[n].Weight)
            .ThenBy(n => n, StringComparer.Ordinal);
}
