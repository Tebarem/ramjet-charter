using Mc.Scripting;
using Mc.Scripting.Commands;
using Mc.Scripting.Permissions;
using Charter.Core;
using Charter.Model;

namespace Charter.Commands;

// /charter user <name|uuid> ...: info, check (read); set, unset, meta, prefix, suffix (shared with groups); group add|remove (membership).
internal sealed partial class CharterTree
{
    private CommandNode UserRoot(Mode mode)
    {
        CommandNode target = Command.Argument("target", Arg.Word()).Suggests(_ => PlayerAndStoreNames());
        if (mode == Mode.Apply)
        {
            target.Then(Command.Literal("info").Permission(ViewNode).Executes(ctx => UserInfo(ctx)));
            target.Then(Command.Literal("check").Permission(ViewNode)
                .Then(Command.Argument("node", Arg.GreedyString()).Suggests(_ => NodeNames()).Executes(ctx => UserCheck(ctx))));
        }
        AttachSubjectWrites(target, mode, ctx => UserSubject(ctx));
        target.Then(UserGroupMembership(mode));
        return Command.Literal("user").Then(target);
    }

    private CommandNode UserGroupMembership(Mode mode)
    {
        string edit = Gate(mode, EditNode);
        return Command.Literal("group").Permission(edit)
            .Then(Command.Literal("add")
                .Then(Command.Argument("group", Arg.Word()).Suggests(_ => GroupNames())
                    .Executes(ctx => Execute(ctx, mode, MembershipAdd(ctx, null)))
                    .Then(Command.Literal("for")
                        .Then(Command.Argument("duration", Arg.Word())
                            .Executes(ctx => Execute(ctx, mode, MembershipAdd(ctx, ctx.GetString("duration"))))))))
            .Then(Command.Literal("remove")
                .Then(Command.Argument("group", Arg.Word()).Suggests(_ => GroupNames())
                    .Executes(ctx => Execute(ctx, mode, MembershipRemove(ctx)))));
    }

    private Subject UserSubject(CommandContext ctx)
    {
        var (id, name) = ResolveUser(ctx);
        return new Subject("user", $"{name} ({id})", id, null, (doc, create) =>
        {
            if (create) return DocEdits.HolderOf(DocEdits.UserFor(doc, id, name));
            return doc.Users.TryGetValue(DocEdits.UserKey(id), out UserDoc? user) ? DocEdits.HolderOf(user) : null;
        });
    }

    private ChangePlan MembershipAdd(CommandContext ctx, string? durationText)
    {
        var (id, name) = ResolveUser(ctx);
        string group = ValidGroupName(ctx.GetString("group"));
        long? seconds = null;
        if (durationText is not null)
        {
            if (!Tokens.TryParseDuration(durationText, out long parsed))
            {
                throw new CommandFailure($"'{durationText}' is not a duration (examples: 30m, 2h, 7d, 1w2d)");
            }
            seconds = parsed;
        }
        long? expires = seconds is { } s ? _now() + s : null;
        string summary = $"Added {name} to {group}{ForSuffix(seconds)}";
        return new ChangePlan("user.group.add", $"{name} ({id}): group {group}{ForSuffix(seconds)}", () => summary,
            doc =>
            {
                if (!doc.Groups.ContainsKey(group)) return $"no group named {group}";
                DocEdits.AddMembership(DocEdits.UserFor(doc, id, name).Groups, group, expires);
                return null;
            },
            id, new[] { group }, null);
    }

    private ChangePlan MembershipRemove(CommandContext ctx)
    {
        var (id, name) = ResolveUser(ctx);
        string group = ValidGroupName(ctx.GetString("group"));
        string summary = $"Removed {name} from {group}";
        return new ChangePlan("user.group.remove", $"{name} ({id}): group {group}", () => summary,
            doc =>
            {
                if (!doc.Users.TryGetValue(DocEdits.UserKey(id), out UserDoc? user)) return $"{name} is not in {group}";
                return DocEdits.RemoveMembership(user.Groups, group) == 0 ? $"{name} is not in {group}" : null;
            },
            id, new[] { group }, null);
    }

    private int UserInfo(CommandContext ctx)
    {
        var (id, name) = ResolveUser(ctx);
        StoreDoc doc = _core.Current;
        long now = _now();
        bool online = Server.GetPlayer(id) is not null;
        ctx.Reply($"{name} ({id}), {(online ? "online" : "offline")}");
        ctx.Reply($"Effective groups: {string.Join(", ", _core.EffectiveGroups(id, _core.ContextOf(id), doc))}");
        if (!doc.Users.TryGetValue(DocEdits.UserKey(id), out UserDoc? user))
        {
            ctx.Reply("Not in the store: no own groups, rules or meta.");
            return 1;
        }
        var groups = user.Groups.Select(m => $"{m.Group} ({Tokens.DescribeExpiry(m.Expires, now)})").ToList();
        ctx.Reply(groups.Count == 0 ? "Own groups: none" : $"Own groups: {string.Join(", ", groups)}");
        ReplyList(ctx, "Rules", user.Rules.Select(r => Tokens.FormatRule(r, now)).ToList());
        ReplyList(ctx, "Meta", user.Meta.Select(m => $"{m.Key} = {m.Value}").ToList());
        ReplyList(ctx, "Prefixes", user.Prefixes.OrderByDescending(a => a.Priority).Select(a => $"{a.Priority}: {a.Text}").ToList());
        ReplyList(ctx, "Suffixes", user.Suffixes.OrderByDescending(a => a.Priority).Select(a => $"{a.Priority}: {a.Text}").ToList());
        return 1;
    }

    private int UserCheck(CommandContext ctx)
    {
        var (id, name) = ResolveUser(ctx);
        RuleTail tail = ParseRule(ctx, allowValue: false, allowFor: false);
        long now = _now();
        QueryContext query = Merge(_core.ContextOf(id), tail.Context);
        Resolution resolution = _core.Resolve(id, tail.Node, query);
        bool has = _core.Has(id, tail.Node, query);
        string layer = resolution.Layer >= 0 ? $", layer {resolution.Layer}" : "";
        string source = resolution.Source is { } src ? $", source {src}" : "";
        string rule = resolution.Rule is { } r ? $", rule {Tokens.FormatRule(r, now)}" : "";
        ctx.Reply($"{name}: {tail.Node} in {Describe(query)}");
        ctx.Reply($"Resolution: {ValueText(resolution.Value)}{layer}{source}{rule}");
        ctx.Reply($"Node default: {DefaultText(tail.Node)}");
        ctx.Reply($"Has: {(has ? "true" : "false")} (after the node default)");
        return has ? 1 : 0;
    }

    private static string ValueText(PermissionValue value) => value switch
    {
        PermissionValue.True => "true",
        PermissionValue.False => "false",
        _ => "unset (no rule matched)",
    };

    private static string DefaultText(string node)
        => PermissionManager.Registered.FirstOrDefault(p => p.Node == node) is { } permission
            ? permission.Default.ToString()
            : "not registered";
}
