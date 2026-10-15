using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using Mc.Scripting.Permissions;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>Feeds the engine's native permission table with the compiled rules of the players who are online. The table answers only for players the
/// server is serving, so offline store users are not sent: they are pushed when they join (and dropped when they leave). A refused message schedules a
/// full resynchronisation (the last state keeps answering in the meantime).</summary>
internal sealed class TableSync
{
    private const int MaxBytesPerMessage = NativePermissionTable.MaxMessageBytes - 64;
    private const long MaxRetryTicks = 200;
    private const int MaxRetryDoublings = 8;

    private readonly CharterCore _core;
    private readonly Scheduler _scheduler;
    private readonly Logger _log;
    private readonly Dictionary<Uuid, List<NativeRule>> _pushed = new();
    private readonly Dictionary<Uuid, uint> _versions = new();
    private ScheduledTask? _retry;
    private int _failures;

    public TableSync(CharterCore core, Scheduler scheduler, Logger log)
    {
        _core = core;
        _scheduler = scheduler;
        _log = log;
    }

    /// <summary>Whether this instance holds the table's claim.</summary>
    public bool Claimed { get; private set; }

    /// <summary>Online subjects the table holds from this instance.</summary>
    public int Subjects => _pushed.Count;

    /// <summary>Messages the engine refused (any non-zero status).</summary>
    public long RefusedMessages { get; private set; }

    /// <summary>Full resynchronisations scheduled after a refusal.</summary>
    public long Retries { get; private set; }

    /// <summary>Claims the table (the built-in backend stops answering command requirements while it is claimed).</summary>
    public bool Claim()
    {
        Claimed = NativePermissionTable.Claim(true);
        if (!Claimed) _log.Error("the engine refused to claim the permission table: the built-in backend keeps answering");
        return Claimed;
    }

    /// <summary>Sends every online player's rules: BeginSnapshot, one subject per player, EndSnapshot. Subjects that are not sent are dropped by the engine.</summary>
    public void Snapshot()
    {
        if (!Claimed) return;
        CancelRetry();
        var map = ContextMap.FromServer();
        var notes = new List<string>();
        bool ok = Send(NativePermissionTable.BeginSnapshot());
        _pushed.Clear();
        foreach (Uuid user in OnlineIds())
        {
            List<NativeRule> rules = _core.CompileSubject(user, map, notes);
            ok &= PushSubject(user, rules);
            _pushed[user] = rules;
        }
        ok &= Send(NativePermissionTable.EndSnapshot());
        _core.LogNotes(notes);
        if (ok) _failures = 0;
        else ScheduleRetry();
    }

    /// <summary>Pushes the subjects whose compiled rules differ from what the table holds, and drops the subjects of players who are no longer online.
    /// Returns the online players whose rules were sent.</summary>
    public List<Uuid> Sync()
    {
        var changed = new List<Uuid>();
        if (!Claimed) return changed;
        var map = ContextMap.FromServer();
        var notes = new List<string>();
        var online = OnlineIds();
        var onlineSet = new HashSet<Uuid>(online);
        foreach (Uuid gone in _pushed.Keys.ToList())
            if (!onlineSet.Contains(gone)) Drop(gone);
        bool ok = true;
        foreach (Uuid user in online)
        {
            List<NativeRule> rules = _core.CompileSubject(user, map, notes);
            if (_pushed.TryGetValue(user, out List<NativeRule>? previous) && SameRules(previous, rules)) continue;
            ok &= PushSubject(user, rules);
            _pushed[user] = rules;
            changed.Add(user);
        }
        _core.LogNotes(notes);
        if (!ok) ScheduleRetry();
        return changed;
    }

    /// <summary>A player joined: pushes the subject if the table does not hold it yet.</summary>
    public void Join(Uuid user)
    {
        if (!Claimed || _pushed.ContainsKey(user)) return;
        var map = ContextMap.FromServer();
        var notes = new List<string>();
        List<NativeRule> rules = _core.CompileSubject(user, map, notes);
        bool ok = PushSubject(user, rules);
        _pushed[user] = rules;
        _core.LogNotes(notes);
        if (!ok) ScheduleRetry();
    }

    /// <summary>A player left: the engine drops the subject.</summary>
    public void Quit(Uuid user)
    {
        if (!Claimed || !_pushed.ContainsKey(user)) return;
        Drop(user);
    }

    /// <summary>Stops the pending resynchronisation (the plugin is unloading).</summary>
    public void CancelRetry()
    {
        _retry?.Cancel();
        _retry = null;
    }

    private void Drop(Uuid user)
    {
        Send(NativePermissionTable.DropSubject(user));
        _pushed.Remove(user);
        _versions.Remove(user);
    }

    // one subject: a SetSubject with the first chunk, then DeltaSubject upserts (each on the version before it) for the rest
    private bool PushSubject(Uuid user, List<NativeRule> rules)
    {
        uint version = _versions.TryGetValue(user, out uint last) ? last + 1 : 1;
        int first = ChunkSize(rules, 0);
        bool ok = Send(NativePermissionTable.SetSubject(user, version, NativePermissionTable.Any, NativePermissionTable.Any, rules.GetRange(0, first)));
        int at = first;
        while (ok && at < rules.Count)
        {
            int count = ChunkSize(rules, at);
            var changes = new List<(bool Upsert, NativeRule Rule)>(count);
            for (int i = at; i < at + count; i++) changes.Add((true, rules[i]));
            uint next = version + 1;
            ok = Send(NativePermissionTable.DeltaSubject(user, version, next, changes));
            version = next;
            at += count;
        }
        _versions[user] = version;
        return ok;
    }

    // how many rules fit in one message: at most 512 and the byte budget (name, six context bytes, layer, value and the op byte of a delta)
    private static int ChunkSize(List<NativeRule> rules, int start)
    {
        int count = 0, bytes = 0;
        for (int i = start; i < rules.Count && count < NativePermissionTable.MaxRulesPerMessage; i++)
        {
            int size = 2 + Encoding.UTF8.GetByteCount(rules[i].Node) + 6 + 2 + 1 + 1;
            if (count > 0 && bytes + size > MaxBytesPerMessage) break;
            bytes += size;
            count++;
        }
        return count;
    }

    private bool Send(byte[] message)
    {
        int status = NativePermissionTable.Apply(message);
        if (status == 0) return true;
        RefusedMessages++;
        _log.Error($"the engine refused a permission table message (status {status}); a full resynchronisation follows");
        return false;
    }

    private void ScheduleRetry()
    {
        if (_retry is { IsCancelled: false }) return;
        _failures = Math.Min(_failures + 1, MaxRetryDoublings + 1);
        long delay = Math.Min(MaxRetryTicks, 1L << (_failures - 1));
        Retries++;
        _retry = _scheduler.RunLater(delay, RetryFired);
    }

    private void RetryFired()
    {
        _retry = null;
        Snapshot();
    }

    private static List<Uuid> OnlineIds()
    {
        var ids = new List<Uuid>();
        foreach (Player player in Server.Players) ids.Add(player.Uuid);
        return ids;
    }

    private static bool SameRules(List<NativeRule> a, List<NativeRule> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            NativeRule x = a[i], y = b[i];
            if (x.Node != y.Node || x.Allow != y.Allow || x.Layer != y.Layer) return false;
            if (!(x.Context ?? Array.Empty<byte>()).AsSpan().SequenceEqual(y.Context ?? Array.Empty<byte>())) return false;
        }
        return true;
    }
}
