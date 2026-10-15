using Mc.Scripting;
using Mc.Scripting.Commands;
using Charter.Model;

namespace Charter.Commands;

// /charter group ...: list (all groups), <name> create|delete|weight|parent|info, plus the shared set/unset/meta/prefix/suffix writes.
internal sealed partial class CharterTree
{
    private const int MaxGroupWeight = 10000;

    private CommandNode GroupRoot(Mode mode)
    {
        var root = Command.Literal("group");
        if (mode == Mode.Apply)
        {
            root.Then(Command.Literal("list").Permission(ViewNode).Executes(ctx => ListGroups(ctx)));
        }

        CommandNode group = Command.Argument("group", Arg.Word()).Suggests(_ => GroupNames());
        string edit = Gate(mode, EditNode);
        group.Then(Command.Literal("create").Permission(edit)
            .Executes(ctx => Execute(ctx, mode, GroupCreate(ctx, 0)))
            .Then(Command.Argument("weight", Arg.Int(0, MaxGroupWeight))
                .Executes(ctx => Execute(ctx, mode, GroupCreate(ctx, ctx.GetInt("weight"))))));
        group.Then(Command.Literal("delete").Permission(edit).Executes(ctx => Execute(ctx, mode, GroupDelete(ctx))));
        group.Then(Command.Literal("weight").Permission(edit)
            .Then(Command.Argument("weight", Arg.Int(0, MaxGroupWeight)).Executes(ctx => Execute(ctx, mode, GroupWeight(ctx)))));
        group.Then(Command.Literal("parent").Permission(edit)
            .Then(Command.Literal("add")
                .Then(Command.Argument("parent", Arg.Word()).Suggests(_ => GroupNames()).Executes(ctx => Execute(ctx, mode, ParentAdd(ctx)))))
            .Then(Command.Literal("remove")
                .Then(Command.Argument("parent", Arg.Word()).Suggests(_ => GroupNames()).Executes(ctx => Execute(ctx, mode, ParentRemove(ctx))))));
        if (mode == Mode.Apply)
        {
            group.Then(Command.Literal("info").Permission(ViewNode).Executes(ctx => GroupInfo(ctx)));
        }
        AttachSubjectWrites(group, mode, ctx => GroupSubject(ctx));
        root.Then(group);
        return root;
    }

    private Subject GroupSubject(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        return new Subject("group", name, null, name, (doc, _) => doc.Groups.TryGetValue(name, out GroupDoc? group) ? DocEdits.HolderOf(group) : null);
    }

    private ChangePlan GroupCreate(CommandContext ctx, int weight)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        string summary = $"Created group {name} (weight {weight})";
        return new ChangePlan("group.create", $"{name} (weight {weight})", () => summary,
            doc =>
            {
                if (doc.Groups.ContainsKey(name)) return $"group {name} already exists";
                doc.Groups[name] = new GroupDoc { Weight = weight };
                return null;
            },
            null, new[] { name }, null);
    }

    private ChangePlan GroupDelete(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        string summary = $"Deleted group {name} (its memberships, parent links and track entries are removed too)";
        return new ChangePlan("group.delete", name, () => summary,
            doc => DocEdits.DeleteGroup(doc, name),
            null, new[] { name }, null);
    }

    private ChangePlan GroupWeight(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        int weight = ctx.GetInt("weight");
        string summary = $"Set the weight of {name} to {weight}";
        return new ChangePlan("group.weight", $"{name} = {weight}", () => summary,
            doc =>
            {
                if (!doc.Groups.TryGetValue(name, out GroupDoc? group)) return $"no group named {name}";
                group.Weight = weight;
                return null;
            },
            null, new[] { name }, null);
    }

    private ChangePlan ParentAdd(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        string parent = ValidGroupName(ctx.GetString("parent"));
        string summary = $"{name} now inherits from {parent}";
        return new ChangePlan("group.parent.add", $"{name} inherits {parent}", () => summary,
            doc =>
            {
                if (!doc.Groups.TryGetValue(name, out GroupDoc? group)) return $"no group named {name}";
                if (!doc.Groups.ContainsKey(parent)) return $"no group named {parent}";
                if (parent == name || DocEdits.ReachesAncestor(doc, parent, name))
                {
                    return $"{name} cannot inherit from {parent}: that would make an inheritance cycle";
                }
                if (group.Parents.Any(p => p.Group == parent)) return $"{name} already inherits from {parent}";
                group.Parents.Add(new Membership { Group = parent });
                return null;
            },
            null, new[] { name, parent }, null);
    }

    private ChangePlan ParentRemove(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        string parent = ValidGroupName(ctx.GetString("parent"));
        string summary = $"{name} no longer inherits from {parent}";
        return new ChangePlan("group.parent.remove", $"{name} inherits {parent} (removed)", () => summary,
            doc =>
            {
                if (!doc.Groups.TryGetValue(name, out GroupDoc? group)) return $"no group named {name}";
                return group.Parents.RemoveAll(p => p.Group == parent) == 0 ? $"{name} does not inherit from {parent}" : null;
            },
            null, new[] { name, parent }, null);
    }

    private int ListGroups(CommandContext ctx)
    {
        var groups = _core.Current.Groups
            .OrderByDescending(g => g.Value.Weight)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
        {
            ctx.Reply("No groups.");
            return 0;
        }
        foreach (var (name, group) in groups.Take(MaxListLines))
        {
            string parents = string.Join(", ", group.Parents.Select(p => p.Group));
            ctx.Reply($"{name}: weight {group.Weight}, inherits [{parents}], {group.Rules.Count} rule(s)");
        }
        if (groups.Count > MaxListLines) ctx.Reply($"... {groups.Count - MaxListLines} more");
        return groups.Count;
    }

    private int GroupInfo(CommandContext ctx)
    {
        string name = ValidGroupName(ctx.GetString("group"));
        StoreDoc doc = _core.Current;
        if (!doc.Groups.TryGetValue(name, out GroupDoc? group)) throw new CommandFailure($"no group named {name}");
        long now = _now();
        int members = doc.Users.Values.Count(u => u.Groups.Any(m => m.Group == name && Tokens.Active(m.Expires, now)));
        ctx.Reply($"Group {name}: weight {group.Weight}, {members} member(s)");
        var parents = group.Parents.Select(p => $"{p.Group} ({Tokens.DescribeExpiry(p.Expires, now)})").ToList();
        ctx.Reply(parents.Count == 0 ? "Inherits from: nothing" : $"Inherits from: {string.Join(", ", parents)}");
        ReplyList(ctx, "Rules", group.Rules.Select(r => Tokens.FormatRule(r, now)).ToList());
        ReplyList(ctx, "Meta", group.Meta.Select(m => $"{m.Key} = {m.Value}").ToList());
        ReplyList(ctx, "Prefixes", group.Prefixes.OrderByDescending(a => a.Priority).Select(a => $"{a.Priority}: {a.Text}").ToList());
        ReplyList(ctx, "Suffixes", group.Suffixes.OrderByDescending(a => a.Priority).Select(a => $"{a.Priority}: {a.Text}").ToList());
        return 1;
    }
}
