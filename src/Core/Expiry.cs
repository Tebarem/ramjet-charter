using Charter.Model;
using System.Collections.Generic;
using System;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>Temporary entries (memberships, rules, meta, affixes) are removed by one scheduled task for the earliest expiry in the store (D4): nothing scans
/// the store per tick. When it fires, every expired entry goes through <see cref="CharterCore"/>'s change pipeline (audited as "expire") and the task is
/// scheduled again for the next one.</summary>
internal sealed class ExpiryScheduler
{
    // the longest single wait: a far expiry is re-checked this often (the clock may move, the store may change)
    private const long MaxWaitTicks = 20L * 3600;

    // a refused expiry (the save failed) is retried this much later, not at every tick
    private const long RetryTicks = 20L * 60;

    private readonly CharterCore _core;
    private readonly Scheduler _scheduler;
    private ScheduledTask? _task;
    private long _due;

    public ExpiryScheduler(CharterCore core, Scheduler scheduler)
    {
        _core = core;
        _scheduler = scheduler;
    }

    /// <summary>The unix time (seconds) the task is waiting for, null when nothing expires.</summary>
    public long? NextDue => _task is { IsCancelled: false } ? _due : null;

    /// <summary>Points the one task at the earliest expiry of the current store (cancels it when there is none).</summary>
    public void Reschedule()
    {
        long? next = Earliest(_core.Current);
        if (next is not { } due)
        {
            Cancel();
            return;
        }
        if (_task is { IsCancelled: false } && _due == due) return;
        Cancel();
        long wait = Math.Max(0, due - CharterCore.NowSeconds());
        long ticks = Math.Clamp(wait * 20 + 1, 1, MaxWaitTicks);
        _due = due;
        _task = _scheduler.RunLater(ticks, Fire);
    }

    private void Fire()
    {
        _task = null;
        if (!_core.ExpireUntil(CharterCore.NowSeconds()).Ok)
        {
            _task = _scheduler.RunLater(RetryTicks, Fire);
            return;
        }
        Reschedule();
    }

    private void Cancel()
    {
        _task?.Cancel();
        _task = null;
    }

    /// <summary>The earliest expiry of any entry of the store (already expired ones included: they fire at the next tick).</summary>
    internal static long? Earliest(StoreDoc doc)
    {
        long? min = null;
        foreach (long? at in AllExpiries(doc))
            if (at is { } x && (min is null || x < min)) min = x;
        return min;
    }

    /// <summary>How many entries have expired at <paramref name="now"/>.</summary>
    internal static int CountExpired(StoreDoc doc, long now)
    {
        int count = 0;
        foreach (long? at in AllExpiries(doc))
            if (Gone(at, now)) count++;
        return count;
    }

    /// <summary>Removes every entry that has expired at <paramref name="now"/>.</summary>
    internal static void RemoveExpired(StoreDoc doc, long now)
    {
        foreach (UserDoc? user in doc.Users.Values)
        {
            if (user is null) continue;
            user.Groups.RemoveAll(m => m is not null && Gone(m.Expires, now));
            user.Rules.RemoveAll(r => r is not null && Gone(r.Expires, now));
            user.Meta.RemoveAll(m => m is not null && Gone(m.Expires, now));
            user.Prefixes.RemoveAll(a => a is not null && Gone(a.Expires, now));
            user.Suffixes.RemoveAll(a => a is not null && Gone(a.Expires, now));
        }
        foreach (GroupDoc? group in doc.Groups.Values)
        {
            if (group is null) continue;
            group.Parents.RemoveAll(m => m is not null && Gone(m.Expires, now));
            group.Rules.RemoveAll(r => r is not null && Gone(r.Expires, now));
            group.Meta.RemoveAll(m => m is not null && Gone(m.Expires, now));
            group.Prefixes.RemoveAll(a => a is not null && Gone(a.Expires, now));
            group.Suffixes.RemoveAll(a => a is not null && Gone(a.Expires, now));
        }
    }

    private static bool Gone(long? expires, long now) => expires is { } at && at <= now;

    private static IEnumerable<long?> AllExpiries(StoreDoc doc)
    {
        foreach (UserDoc? user in doc.Users.Values)
        {
            if (user is null) continue;
            foreach (Membership? m in user.Groups) if (m is not null) yield return m.Expires;
            foreach (RuleDoc? r in user.Rules) if (r is not null) yield return r.Expires;
            foreach (MetaDoc? m in user.Meta) if (m is not null) yield return m.Expires;
            foreach (AffixDoc? a in user.Prefixes) if (a is not null) yield return a.Expires;
            foreach (AffixDoc? a in user.Suffixes) if (a is not null) yield return a.Expires;
        }
        foreach (GroupDoc? group in doc.Groups.Values)
        {
            if (group is null) continue;
            foreach (Membership? m in group.Parents) if (m is not null) yield return m.Expires;
            foreach (RuleDoc? r in group.Rules) if (r is not null) yield return r.Expires;
            foreach (MetaDoc? m in group.Meta) if (m is not null) yield return m.Expires;
            foreach (AffixDoc? a in group.Prefixes) if (a is not null) yield return a.Expires;
            foreach (AffixDoc? a in group.Suffixes) if (a is not null) yield return a.Expires;
        }
    }
}
