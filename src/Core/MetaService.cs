using Charter.Model;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System;
using Mc.Scripting.Services;
using Mc.Scripting;

namespace Charter.Core;

/// <summary><see cref="IPlayerMeta"/> over the store: typed meta values and prefixes or suffixes, resolved with the same plan as the permissions
/// (the user's own entries first, then the groups in layer order), and filtered by the player's current context.</summary>
public sealed class MetaService : IPlayerMeta
{
    private readonly CharterCore _core;

    public MetaService(CharterCore core) => _core = core;

    /// <inheritdoc/>
    public event Action<Uuid?>? Changed
    {
        add => _core.Changed += value;
        remove => _core.Changed -= value;
    }

    /// <inheritdoc/>
    /// <remarks>The lowest layer with a live entry of this key that matches the player's context wins; inside a layer the most specific context wins. An
    /// entry whose text does not parse as <typeparamref name="T"/> is skipped (a wrong type is not a value of that key).</remarks>
    public bool TryGet<T>(Uuid player, MetaKey<T> key, out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        long now = CharterCore.NowSeconds();
        QueryContext ctx = _core.ContextOf(player);
        Plan plan = Resolver.BuildPlan(_core.Current, player, now);
        bool found = false;
        T result = default!;
        Entry<MetaDoc>? best = null;
        foreach (Entry<MetaDoc> e in Resolver.Collect<MetaDoc>(plan, now, u => u.Meta, g => g.Meta, m => m.Context, m => m.Expires))
        {
            if (e.Item.Key != key.Name || !e.Ctx.Matches(ctx)) continue;
            if (!TryParse(e.Item.Value, out T parsed)) continue;
            if (best is { } b && !(e.Layer < b.Layer || (e.Layer == b.Layer && e.Ctx.Specificity > b.Ctx.Specificity))) continue;
            best = e;
            result = parsed;
            found = true;
        }
        value = result;
        return found;
    }

    /// <inheritdoc/>
    /// <remarks>The player's own affixes and those of the groups in the player's chain that apply in the current context, highest priority first. Ties keep
    /// the layer order (the user first). A user's affix is attributed to "user".</remarks>
    public IReadOnlyList<Affix> Affixes(Uuid player, AffixKind kind)
    {
        long now = CharterCore.NowSeconds();
        QueryContext ctx = _core.ContextOf(player);
        Plan plan = Resolver.BuildPlan(_core.Current, player, now);
        return Resolver.Collect<AffixDoc>(plan, now,
                u => kind == AffixKind.Prefix ? u.Prefixes : u.Suffixes,
                g => kind == AffixKind.Prefix ? g.Prefixes : g.Suffixes,
                a => a.Context, a => a.Expires)
            .Where(e => e.Ctx.Matches(ctx))
            .OrderByDescending(e => e.Item.Priority)
            .ThenBy(e => e.Layer)
            .Select(e => new Affix(e.Item.Text, e.Item.Priority, e.Source))
            .ToList();
    }

    /// <summary>Parses a stored meta value as the key's type: text as is; int, bool (true/false) and TimeSpan (seconds, or "1h30m" with d, h, m, s).</summary>
    internal static bool TryParse<T>(string text, out T value)
    {
        value = default!;
        object? boxed = null;
        if (typeof(T) == typeof(string)) boxed = text;
        else if (typeof(T) == typeof(int))
        {
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i)) boxed = i;
        }
        else if (typeof(T) == typeof(bool))
        {
            if (bool.TryParse(text.Trim(), out bool b)) boxed = b;
        }
        else if (typeof(T) == typeof(TimeSpan))
        {
            if (TryDuration(text, out TimeSpan span)) boxed = span;
        }
        if (boxed is T typed)
        {
            value = typed;
            return true;
        }
        return false;
    }

    /// <summary>A duration: a number of seconds, or a sequence of number and unit (d, h, m, s), e.g. "1h30m".</summary>
    internal static bool TryDuration(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        string s = text.Trim();
        if (s.Length == 0) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            if (double.IsNaN(seconds) || seconds < 0) return false;
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }
        double total = 0;
        int components = 0;
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.')) i++;
            if (i == start || i >= s.Length) return false;
            if (!double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)) return false;
            double unit = char.ToLowerInvariant(s[i]) switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                's' => 1,
                _ => -1,
            };
            if (unit < 0) return false;
            total += amount * unit;
            components++;
            i++;
        }
        if (components == 0) return false;
        value = TimeSpan.FromSeconds(total);
        return true;
    }
}
