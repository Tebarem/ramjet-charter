using Mc.Scripting;
using Mc.Scripting.Commands;
using Charter.Model;

namespace Charter.Commands;

// /charter track ...: list (all tracks), <name> create|delete|append|promote|demote|list.
// Promote: a user on no track group joins the lowest group; otherwise the user's highest track group moves one step up. At the top: error.
// Demote: the user's highest track group moves one step down; at the bottom the user leaves the track. Not on the track: error.
internal sealed partial class CharterTree
{
    private CommandNode TrackRoot(Mode mode)
    {
        var root = Command.Literal("track");
        if (mode == Mode.Apply)
        {
            root.Then(Command.Literal("list").Permission(ViewNode).Executes(ctx => ListTracks(ctx)));
        }

        CommandNode track = Command.Argument("track", Arg.Word()).Suggests(_ => TrackNames());
        string edit = Gate(mode, EditNode);
        track.Then(Command.Literal("create").Permission(edit).Executes(ctx => Execute(ctx, mode, TrackCreate(ctx))));
        track.Then(Command.Literal("delete").Permission(edit).Executes(ctx => Execute(ctx, mode, TrackDelete(ctx))));
        track.Then(Command.Literal("append").Permission(edit)
            .Then(Command.Argument("group", Arg.Word()).Suggests(_ => GroupNames()).Executes(ctx => Execute(ctx, mode, TrackAppend(ctx)))));
        track.Then(Command.Literal("promote").Permission(edit)
            .Then(Command.Argument("target", Arg.Word()).Suggests(_ => PlayerAndStoreNames())
                .Executes(ctx => Execute(ctx, mode, TrackMove(ctx, promote: true)))));
        track.Then(Command.Literal("demote").Permission(edit)
            .Then(Command.Argument("target", Arg.Word()).Suggests(_ => PlayerAndStoreNames())
                .Executes(ctx => Execute(ctx, mode, TrackMove(ctx, promote: false)))));
        if (mode == Mode.Apply)
        {
            track.Then(Command.Literal("list").Permission(ViewNode).Executes(ctx => ShowTrack(ctx)));
        }
        root.Then(track);
        return root;
    }

    private IReadOnlyList<string> TrackGroups(string track)
        => _core.Current.Tracks.TryGetValue(track, out TrackDoc? doc) ? new List<string>(doc.Groups) : new List<string>();

    private ChangePlan TrackCreate(CommandContext ctx)
    {
        string name = ValidTrackName(ctx.GetString("track"));
        string summary = $"Created track {name}";
        return new ChangePlan("track.create", name, () => summary,
            doc =>
            {
                if (doc.Tracks.ContainsKey(name)) return $"track {name} already exists";
                doc.Tracks[name] = new TrackDoc();
                return null;
            },
            null, Array.Empty<string>(), null);
    }

    private ChangePlan TrackDelete(CommandContext ctx)
    {
        string name = ValidTrackName(ctx.GetString("track"));
        string summary = $"Deleted track {name}";
        return new ChangePlan("track.delete", name, () => summary,
            doc => doc.Tracks.Remove(name) ? null : $"no track named {name}",
            null, TrackGroups(name), null);
    }

    private ChangePlan TrackAppend(CommandContext ctx)
    {
        string track = ValidTrackName(ctx.GetString("track"));
        string group = ValidGroupName(ctx.GetString("group"));
        string summary = $"Appended {group} to track {track}";
        return new ChangePlan("track.append", $"{track}: {group}", () => summary,
            doc =>
            {
                if (!doc.Tracks.TryGetValue(track, out TrackDoc? t)) return $"no track named {track}";
                if (!doc.Groups.ContainsKey(group)) return $"no group named {group}";
                if (t.Groups.Contains(group)) return $"{group} is already on track {track}";
                t.Groups.Add(group);
                return null;
            },
            null, new[] { group }.Concat(TrackGroups(track)).ToArray(), null);
    }

    private ChangePlan TrackMove(CommandContext ctx, bool promote)
    {
        string track = ValidTrackName(ctx.GetString("track"));
        var (id, name) = ResolveUser(ctx);
        long now = _now();
        string key = DocEdits.UserKey(id);
        IReadOnlyList<string> groups = TrackGroups(track);
        string moved = "";
        return new ChangePlan(promote ? "track.promote" : "track.demote", $"{name} ({id}): {track}", () => moved,
            doc =>
            {
                if (!doc.Tracks.TryGetValue(track, out TrackDoc? t)) return $"no track named {track}";
                if (t.Groups.Count == 0) return $"track {track} has no groups";
                int index = DocEdits.TrackIndex(doc, key, t.Groups, now);
                string? from = index >= 0 ? t.Groups[index] : null;
                string? to;
                if (promote)
                {
                    if (index == t.Groups.Count - 1) return $"{name} is already at the top of {track} ({from})";
                    to = index < 0 ? t.Groups[0] : t.Groups[index + 1];
                }
                else
                {
                    if (index < 0) return $"{name} is not on track {track}";
                    to = index == 0 ? null : t.Groups[index - 1];
                }
                if (to is not null && !doc.Groups.ContainsKey(to)) return $"no group named {to} (on track {track})";

                UserDoc user = DocEdits.UserFor(doc, id, name);
                if (from is not null) DocEdits.RemoveMembership(user.Groups, from);
                if (to is not null) DocEdits.AddMembership(user.Groups, to, null);
                moved = (from, to) switch
                {
                    (null, not null) => $"{name} joined {track} at {to}",
                    (not null, null) => $"{name} left {track} (was {from})",
                    _ => $"{name} {(promote ? "promoted" : "demoted")} on {track}: {from} -> {to}",
                };
                return null;
            },
            id, groups, null);
    }

    private int ListTracks(CommandContext ctx)
    {
        var tracks = _core.Current.Tracks.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        if (tracks.Count == 0)
        {
            ctx.Reply("No tracks.");
            return 0;
        }
        foreach (var (name, track) in tracks.Take(MaxListLines))
        {
            ctx.Reply(track.Groups.Count == 0 ? $"{name}: (no groups)" : $"{name}: {string.Join(" -> ", track.Groups)}");
        }
        if (tracks.Count > MaxListLines) ctx.Reply($"... {tracks.Count - MaxListLines} more");
        return tracks.Count;
    }

    private int ShowTrack(CommandContext ctx)
    {
        string name = ValidTrackName(ctx.GetString("track"));
        if (!_core.Current.Tracks.TryGetValue(name, out TrackDoc? track)) throw new CommandFailure($"no track named {name}");
        ctx.Reply(track.Groups.Count == 0
            ? $"Track {name}: no groups"
            : $"Track {name} (lowest first): {string.Join(" -> ", track.Groups)}");
        return track.Groups.Count;
    }
}
