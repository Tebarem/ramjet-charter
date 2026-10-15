using Mc.Scripting;
using Mc.Scripting.Commands;
using Mc.Scripting.Permissions;
using Charter.Audit;
using Charter.Core;
using Charter.Model;

namespace Charter.Commands;

// The /charter tree (also registered as /perm). This file: the root, the shared helpers (execution, subjects, the rule, meta and affix
// writes that users and groups have in common), status and reload. Users, groups, tracks, audit and import live in the other partial files.
//
// Every write is built once per command line as a ChangePlan and then run in one of two modes: Apply goes through core.Change; Preview
// (under /charter preview) runs the same edit on a copy and prints the gains and losses. The Preview tree is the same tree with the read
// leaves left out and the gates set to charter.admin.view, so a change is previewed with exactly the grammar it is applied with.
internal sealed partial class CharterTree
{
    public const string ViewNode = "charter.admin.view";
    public const string EditNode = "charter.admin.edit";
    public const string AuditNode = "charter.admin.audit";
    public const string ImportNode = "charter.admin.import";

    private const int MaxListLines = 40;

    private readonly ICharterCore _core;
    private readonly AuditLog _audit;
    private readonly Func<long> _now;

    /// <param name="core">The Core seam (store, resolver, changes).</param>
    /// <param name="audit">The audit log, read by /charter audit.</param>
    /// <param name="now">Unix seconds; injectable so tests can use a fake clock.</param>
    public CharterTree(ICharterCore core, AuditLog audit, Func<long>? now = null)
    {
        _core = core;
        _audit = audit;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>The whole tree under a root literal. Call once per root name: nodes cannot be shared between two registrations.</summary>
    public CommandNode Root(string name)
    {
        return Command.Literal(name).Permission(ViewNode)
            .Then(UserRoot(Mode.Apply))
            .Then(GroupRoot(Mode.Apply))
            .Then(TrackRoot(Mode.Apply))
            .Then(AuditRoot())
            .Then(ImportRoot())
            .Then(ExportRoot())
            .Then(Command.Literal("status").Executes(ctx => Status(ctx)))
            .Then(Command.Literal("reload").Permission(EditNode).Executes(ctx => Reload(ctx)))
            .Then(Command.Literal("preview")
                .Then(UserRoot(Mode.Preview))
                .Then(GroupRoot(Mode.Preview))
                .Then(TrackRoot(Mode.Preview)));
    }

    /// <summary>The permission node a write needs in Apply mode; a dry run needs only view.</summary>
    private static string Gate(Mode mode, string writeNode) => mode == Mode.Apply ? writeNode : ViewNode;

    private static Actor ActorOf(CommandContext ctx) => ctx.Player is { } player ? Actor.Player(player) : Actor.Console;

    /// <summary>Runs a plan: a dry run prints the diff; Apply commits through Core and reports the result or the refusal.</summary>
    private int Execute(CommandContext ctx, Mode mode, ChangePlan plan)
    {
        if (mode == Mode.Preview)
        {
            IReadOnlyList<ChangePreview.Subject> subjects = plan.User is { } user
                ? new[] { SubjectForUser(user) }
                : ChangePreview.OnlinePlayers(_core);
            ChangePreview.Run(_core, plan, subjects, line => ctx.Reply(line));
            return 1;
        }
        ChangeResult result = _core.Change(ActorOf(ctx), plan.Action, plan.Detail, plan.Edit);
        if (!result.Ok) throw new CommandFailure(result.Error ?? "the change was refused");
        ctx.Reply($"{plan.Summary()} ({result.SubjectsChanged} changed)");
        return 1;
    }

    private ChangePreview.Subject SubjectForUser(Uuid id)
    {
        string name = Server.GetPlayer(id)?.Name
            ?? (_core.Current.Users.TryGetValue(DocEdits.UserKey(id), out UserDoc? stored) && stored.Name is { } known ? known : id.ToString());
        return new ChangePreview.Subject(id, name, _core.ContextOf(id));
    }

    /// <summary>The target of a rule, meta or affix write: a user (created on first write) or an existing group.</summary>
    /// <param name="Kind">"user" or "group" (the audit action prefix).</param>
    /// <param name="Display">The text shown and written to the audit detail (the name first, so audit can filter on it).</param>
    /// <param name="User">The user, when the subject is a user.</param>
    /// <param name="Group">The group, when the subject is a group.</param>
    /// <param name="Find">Returns the lists to write to; create asks for a user to be created when missing.</param>
    private sealed record Subject(string Kind, string Display, Uuid? User, string? Group, Func<StoreDoc, bool, DocEdits.Holder?> Find);

    private (Uuid Id, string Name) ResolveUser(CommandContext ctx)
    {
        string text = ctx.GetString("target");
        if (!_core.TryFindUser(text, out Uuid id, out string name)) throw new CommandFailure($"No user named {text}");
        return (id, name);
    }

    private static string ValidGroupName(string raw)
    {
        string name = raw.ToLowerInvariant();
        if (!Tokens.IsValidName(name))
        {
            throw new CommandFailure($"'{raw}' is not a valid group name (lower-case letters, digits, '_' and '-', up to 32 characters)");
        }
        return name;
    }

    private static string ValidTrackName(string raw)
    {
        string name = raw.ToLowerInvariant();
        if (!Tokens.IsValidName(name))
        {
            throw new CommandFailure($"'{raw}' is not a valid track name (lower-case letters, digits, '_' and '-', up to 32 characters)");
        }
        return name;
    }

    private static string NotFound(Subject subject)
        => subject.Kind == "group" ? $"no group named {subject.Group}" : $"{subject.Display} is not in the store";

    private RuleTail ParseRule(CommandContext ctx, bool allowValue, bool allowFor)
    {
        RuleTail? tail = Tokens.ParseRuleText(ctx.GetString("node"), allowValue, allowFor, _core.Current.Slots, out string? error);
        return tail ?? throw new CommandFailure(error ?? "invalid rule");
    }

    private static IReadOnlyList<string> GroupsOf(string? group) => group is null ? Array.Empty<string>() : new[] { group };

    private static string ForSuffix(long? seconds) => seconds is { } s ? $" for {Tokens.FormatDuration(s)}" : "";

    /// <summary>set, unset, meta, prefix and suffix: the writes that users and groups share. Attached under the subject's argument node.</summary>
    private void AttachSubjectWrites(CommandNode parent, Mode mode, Func<CommandContext, Subject> subjectOf)
    {
        string edit = Gate(mode, EditNode);
        parent.Then(Command.Literal("set").Permission(edit)
            .Then(Command.Argument("node", Arg.GreedyString()).Suggests(_ => NodeNames())
                .Executes(ctx => Execute(ctx, mode, SetRule(subjectOf(ctx), ctx)))));
        parent.Then(Command.Literal("unset").Permission(edit)
            .Then(Command.Argument("node", Arg.GreedyString()).Suggests(_ => NodeNames())
                .Executes(ctx => Execute(ctx, mode, UnsetRule(subjectOf(ctx), ctx)))));
        parent.Then(Command.Literal("meta").Permission(edit)
            .Then(Command.Literal("set")
                .Then(Command.Argument("key", Arg.Word())
                    .Executes(ctx => Execute(ctx, mode, MetaSet(subjectOf(ctx), ctx.GetString("key"), "")))
                    .Then(Command.Argument("value", Arg.GreedyString())
                        .Executes(ctx => Execute(ctx, mode, MetaSet(subjectOf(ctx), ctx.GetString("key"), ctx.GetString("value")))))))
            .Then(Command.Literal("unset")
                .Then(Command.Argument("key", Arg.Word())
                    .Executes(ctx => Execute(ctx, mode, MetaUnset(subjectOf(ctx), ctx.GetString("key")))))));
        parent.Then(AffixNode("prefix", "prefixes", edit, mode, subjectOf));
        parent.Then(AffixNode("suffix", "suffixes", edit, mode, subjectOf));
    }

    private CommandNode AffixNode(string kind, string listName, string edit, Mode mode, Func<CommandContext, Subject> subjectOf)
    {
        bool prefix = kind == "prefix";
        return Command.Literal(kind).Permission(edit)
            .Then(Command.Literal("add")
                .Then(Command.Argument("priority", Arg.Int(-1000000, 1000000))
                    .Then(Command.Argument("text", Arg.GreedyString())
                        .Executes(ctx => Execute(ctx, mode, AffixSet(subjectOf(ctx), prefix, ctx.GetInt("priority"), ctx.GetString("text")))))))
            .Then(Command.Literal("remove")
                .Then(Command.Argument("priority", Arg.Int(-1000000, 1000000))
                    .Executes(ctx => Execute(ctx, mode, AffixUnset(subjectOf(ctx), prefix, ctx.GetInt("priority"))))));
    }

    private ChangePlan SetRule(Subject subject, CommandContext ctx)
    {
        RuleTail tail = ParseRule(ctx, allowValue: true, allowFor: true);
        bool value = tail.Value ?? true;
        long? expires = tail.Seconds is { } seconds ? _now() + seconds : null;
        string what = $"{tail.Node} = {(value ? "true" : "false")}{Tokens.FormatContext(tail.Context)}{ForSuffix(tail.Seconds)}";
        string summary = $"Set {what} on {subject.Display}";
        return new ChangePlan($"{subject.Kind}.set", $"{subject.Display}: {what}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, true);
                if (holder is null) return NotFound(subject);
                DocEdits.SetRule(holder.Rules, tail.Node, value, tail.Context, expires);
                return null;
            },
            subject.User, GroupsOf(subject.Group), tail.Node);
    }

    private ChangePlan UnsetRule(Subject subject, CommandContext ctx)
    {
        RuleTail tail = ParseRule(ctx, allowValue: false, allowFor: false);
        string what = $"{tail.Node}{Tokens.FormatContext(tail.Context)}";
        string summary = $"Removed {what} from {subject.Display}";
        return new ChangePlan($"{subject.Kind}.unset", $"{subject.Display}: {what}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, false);
                if (holder is null) return NotFound(subject);
                return DocEdits.UnsetRule(holder.Rules, tail.Node, tail.Context) == 0
                    ? $"{subject.Display} has no rule {what}"
                    : null;
            },
            subject.User, GroupsOf(subject.Group), tail.Node);
    }

    private ChangePlan MetaSet(Subject subject, string key, string value)
    {
        if (!Tokens.IsValidMetaKey(key)) throw new CommandFailure($"'{key}' is not a valid meta key (letters, digits, '_', '.', '-', ':')");
        string summary = $"Set meta {key} on {subject.Display}";
        return new ChangePlan($"{subject.Kind}.meta.set", $"{subject.Display}: {key} = {value}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, true);
                if (holder is null) return NotFound(subject);
                DocEdits.SetMeta(holder.Meta, key, value);
                return null;
            },
            subject.User, Array.Empty<string>(), null);
    }

    private ChangePlan MetaUnset(Subject subject, string key)
    {
        if (!Tokens.IsValidMetaKey(key)) throw new CommandFailure($"'{key}' is not a valid meta key (letters, digits, '_', '.', '-', ':')");
        string summary = $"Removed meta {key} from {subject.Display}";
        return new ChangePlan($"{subject.Kind}.meta.unset", $"{subject.Display}: {key}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, false);
                if (holder is null) return NotFound(subject);
                return DocEdits.UnsetMeta(holder.Meta, key) ? null : $"{subject.Display} has no meta {key}";
            },
            subject.User, Array.Empty<string>(), null);
    }

    private ChangePlan AffixSet(Subject subject, bool prefix, int priority, string text)
    {
        string side = prefix ? "prefix" : "suffix";
        string summary = $"Set {side} {priority} on {subject.Display}";
        return new ChangePlan($"{subject.Kind}.{side}.add", $"{subject.Display}: {side} {priority} {text}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, true);
                if (holder is null) return NotFound(subject);
                DocEdits.SetAffix(prefix ? holder.Prefixes : holder.Suffixes, priority, text);
                return null;
            },
            subject.User, Array.Empty<string>(), null);
    }

    private ChangePlan AffixUnset(Subject subject, bool prefix, int priority)
    {
        string side = prefix ? "prefix" : "suffix";
        string summary = $"Removed {side} {priority} from {subject.Display}";
        return new ChangePlan($"{subject.Kind}.{side}.remove", $"{subject.Display}: {side} {priority}", () => summary,
            doc =>
            {
                DocEdits.Holder? holder = subject.Find(doc, false);
                if (holder is null) return NotFound(subject);
                bool removed = DocEdits.UnsetAffix(prefix ? holder.Prefixes : holder.Suffixes, priority);
                return removed ? null : $"{subject.Display} has no {side} at priority {priority}";
            },
            subject.User, Array.Empty<string>(), null);
    }

    private int Status(CommandContext ctx)
    {
        foreach (var (key, value) in _core.Status()) ctx.Reply($"{key}: {value}");
        return 1;
    }

    private int Reload(CommandContext ctx)
    {
        ChangeResult result = _core.Reload();
        if (!result.Ok) throw new CommandFailure(result.Error ?? "reload failed");
        ctx.Reply($"Reloaded store.json: {_core.Current.Groups.Count} group(s), {_core.Current.Users.Count} user(s)");
        return 1;
    }

    private IEnumerable<string> PlayerAndStoreNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Player player in Server.Players) names.Add(player.Name);
        foreach (UserDoc user in _core.Current.Users.Values)
        {
            if (user.Name is { Length: > 0 } name) names.Add(name);
        }
        return names;
    }

    private IEnumerable<string> NodeNames()
        => PermissionManager.Registered.Select(p => p.Node).Append("*");

    private IEnumerable<string> GroupNames() => _core.Current.Groups.Keys;

    private IEnumerable<string> TrackNames() => _core.Current.Tracks.Keys;

    private static string Describe(QueryContext q)
    {
        var parts = new List<string>(6);
        if (q.World is not null) parts.Add($"world={q.World}");
        if (q.Dimension is not null) parts.Add($"dimension={q.Dimension}");
        if (q.Gamemode is not null) parts.Add($"gamemode={q.Gamemode}");
        if (q.Server is not null) parts.Add($"server={q.Server}");
        if (q.Slot0 is not null) parts.Add($"slot0={q.Slot0}");
        if (q.Slot1 is not null) parts.Add($"slot1={q.Slot1}");
        return parts.Count == 0 ? "no context" : string.Join(" ", parts);
    }

    /// <summary>The live context with the positions the command typed overriding it.</summary>
    private static QueryContext Merge(QueryContext live, ContextDoc? typed)
    {
        if (typed is null) return live;
        return new QueryContext(
            typed.World ?? live.World,
            typed.Dimension ?? live.Dimension,
            typed.Gamemode ?? live.Gamemode,
            typed.Server ?? live.Server,
            typed.Slot0 ?? live.Slot0,
            typed.Slot1 ?? live.Slot1);
    }

    private static void ReplyList(CommandContext ctx, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        ctx.Reply($"{label} ({items.Count}):");
        foreach (string item in items.Take(MaxListLines)) ctx.Reply($"  {item}");
        if (items.Count > MaxListLines) ctx.Reply($"  ... {items.Count - MaxListLines} more");
    }
}
