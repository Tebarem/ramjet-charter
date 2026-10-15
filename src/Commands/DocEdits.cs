using Mc.Scripting;
using Charter.Model;

namespace Charter.Commands;

/// <summary>Pure edits of a <see cref="StoreDoc"/> copy. Every change the commands make goes through here, so the apply path and the
/// dry-run path (<c>/charter preview</c>) run the same code. Each method returns an error text, or null when the edit applied.</summary>
internal static class DocEdits
{
    /// <summary>The store key of a user (lower-case, dashed uuid text).</summary>
    public static string UserKey(Uuid id) => id.ToString();

    /// <summary>Two context patterns are the same when every position matches; null and "any" are the same.</summary>
    public static bool SameContext(ContextDoc? a, ContextDoc? b)
    {
        if (a is null || a.IsAny) return b is null || b.IsAny;
        if (b is null || b.IsAny) return false;
        return a.World == b.World && a.Dimension == b.Dimension && a.Gamemode == b.Gamemode
            && a.Server == b.Server && a.Slot0 == b.Slot0 && a.Slot1 == b.Slot1;
    }

    /// <summary>Sets node = value for a context: replaces the rule with the same node and context.</summary>
    public static void SetRule(List<RuleDoc> rules, string node, bool value, ContextDoc? context, long? expires)
    {
        rules.RemoveAll(r => r.Node == node && SameContext(r.Context, context));
        rules.Add(new RuleDoc { Node = node, Value = value, Context = context, Expires = expires });
    }

    public static int UnsetRule(List<RuleDoc> rules, string node, ContextDoc? context)
        => rules.RemoveAll(r => r.Node == node && SameContext(r.Context, context));

    /// <summary>Adds (or re-times) a membership in a group. Context-free memberships only: the commands never create context-bound ones.</summary>
    public static void AddMembership(List<Membership> memberships, string group, long? expires)
    {
        memberships.RemoveAll(m => m.Group == group && m.Context is null);
        memberships.Add(new Membership { Group = group, Expires = expires });
    }

    public static int RemoveMembership(List<Membership> memberships, string group)
        => memberships.RemoveAll(m => m.Group == group);

    /// <summary>Sets a context-free meta entry.</summary>
    public static void SetMeta(List<MetaDoc> meta, string key, string value)
    {
        meta.RemoveAll(m => m.Key == key && m.Context is null);
        meta.Add(new MetaDoc { Key = key, Value = value });
    }

    public static bool UnsetMeta(List<MetaDoc> meta, string key)
        => meta.RemoveAll(m => m.Key == key && m.Context is null) > 0;

    /// <summary>Sets a prefix or suffix at a priority (one text per priority).</summary>
    public static void SetAffix(List<AffixDoc> affixes, int priority, string text)
    {
        affixes.RemoveAll(a => a.Priority == priority && a.Context is null);
        affixes.Add(new AffixDoc { Text = text, Priority = priority });
    }

    public static bool UnsetAffix(List<AffixDoc> affixes, int priority)
        => affixes.RemoveAll(a => a.Priority == priority && a.Context is null) > 0;

    /// <summary>True when <paramref name="from"/> reaches <paramref name="target"/> through parent links (cycle check).</summary>
    public static bool ReachesAncestor(StoreDoc doc, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (current == target) return true;
            if (!seen.Add(current)) continue;
            if (!doc.Groups.TryGetValue(current, out GroupDoc? group)) continue;
            foreach (Membership parent in group.Parents) stack.Push(parent.Group);
        }
        return false;
    }

    /// <summary>The group and all its ancestors (parent links), for the nodes a group change can affect.</summary>
    public static IEnumerable<string> Closure(StoreDoc doc, IEnumerable<string> groups)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(groups);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (!seen.Add(current)) continue;
            if (!doc.Groups.TryGetValue(current, out GroupDoc? group)) continue;
            foreach (Membership parent in group.Parents) stack.Push(parent.Group);
        }
        return seen;
    }

    /// <summary>Deletes a group and every reference to it (memberships, parent links, track entries).</summary>
    public static string? DeleteGroup(StoreDoc doc, string name)
    {
        if (name == "default") return "the default group cannot be deleted";
        if (!doc.Groups.Remove(name)) return $"no group named {name}";
        foreach (UserDoc user in doc.Users.Values) user.Groups.RemoveAll(m => m.Group == name);
        foreach (GroupDoc group in doc.Groups.Values) group.Parents.RemoveAll(m => m.Group == name);
        foreach (TrackDoc track in doc.Tracks.Values) track.Groups.RemoveAll(g => g == name);
        return null;
    }

    /// <summary>Index on the track of the user's highest group that is in force, or -1 when the user is on none of the track's groups.</summary>
    public static int TrackIndex(StoreDoc doc, string userKey, IReadOnlyList<string> trackGroups, long now)
    {
        if (!doc.Users.TryGetValue(userKey, out UserDoc? user)) return -1;
        for (int i = trackGroups.Count - 1; i >= 0; i--)
        {
            if (user.Groups.Any(m => m.Group == trackGroups[i] && Tokens.Active(m.Expires, now))) return i;
        }
        return -1;
    }

    /// <summary>Rules, meta and affixes of a subject: the lists a set/unset/meta/prefix/suffix command writes to.</summary>
    public sealed record Holder(List<RuleDoc> Rules, List<MetaDoc> Meta, List<AffixDoc> Prefixes, List<AffixDoc> Suffixes);

    public static Holder HolderOf(UserDoc user) => new(user.Rules, user.Meta, user.Prefixes, user.Suffixes);

    public static Holder HolderOf(GroupDoc group) => new(group.Rules, group.Meta, group.Prefixes, group.Suffixes);

    /// <summary>The user's entry, created when missing (writes to a user the store has never seen).</summary>
    public static UserDoc UserFor(StoreDoc doc, Uuid id, string? name)
    {
        string key = UserKey(id);
        if (!doc.Users.TryGetValue(key, out UserDoc? user))
        {
            user = new UserDoc();
            doc.Users[key] = user;
        }
        if (name is not null) user.Name = name;
        return user;
    }

    /// <summary>Copies every field of <paramref name="source"/> into <paramref name="target"/> (import --apply replaces the document this way).</summary>
    public static void ReplaceContents(StoreDoc target, StoreDoc source)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.ServerId = source.ServerId;
        target.Groups = source.Groups;
        target.Users = source.Users;
        target.Tracks = source.Tracks;
        target.Slots = source.Slots;
        target.Extra = source.Extra;
    }
}
