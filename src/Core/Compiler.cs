using Charter.Model;
using System.Collections.Generic;
using System.Linq;
using System;
using Mc.Scripting.Permissions;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>Names to the native context bytes: world index (from the server's dimensions), dimension, game mode, server id and the two slot values.</summary>
public sealed class ContextMap
{
    private readonly Dictionary<string, byte> _worlds = new(StringComparer.Ordinal);

    /// <param name="worlds">World keys (as the server names them) and their dimension index.</param>
    public ContextMap(IEnumerable<KeyValuePair<string, byte>> worlds)
    {
        foreach (var (key, index) in worlds) _worlds[Resolver.NormWorld(key)] = index;
    }

    /// <summary>The map of the running server: every dimension, by its key, with its slot as the index.</summary>
    public static ContextMap FromServer()
    {
        var list = new List<KeyValuePair<string, byte>>();
        foreach (World world in Server.Worlds)
        {
            string key = world.Key;
            if (key.Length == 0 || world.Index < 0 || world.Index > 254) continue;
            list.Add(new KeyValuePair<string, byte>(key, (byte)world.Index));
        }
        return new ContextMap(list);
    }

    /// <summary>The index of a world name, false when the server has no such world.</summary>
    public bool TryWorld(string name, out byte index) => _worlds.TryGetValue(Resolver.NormWorld(name), out index);

    /// <summary>overworld 0, the_nether 1, the_end 2, other 3; <see cref="NativePermissionTable.Any"/> for an unknown name.</summary>
    public static byte Dimension(string? name) => name switch
    {
        "overworld" => (byte)0,
        "the_nether" => (byte)1,
        "the_end" => (byte)2,
        "other" => (byte)3,
        _ => NativePermissionTable.Any,
    };

    /// <summary>survival 0, creative 1, adventure 2, spectator 3; <see cref="NativePermissionTable.Any"/> for an unknown name.</summary>
    public static byte GameMode(string? name) => name switch
    {
        "survival" => (byte)0,
        "creative" => (byte)1,
        "adventure" => (byte)2,
        "spectator" => (byte)3,
        _ => NativePermissionTable.Any,
    };

    /// <summary>The dimension name of a world key ("minecraft:the_nether" is the_nether; any other key is other).</summary>
    public static string DimensionOf(string worldKey)
    {
        string key = worldKey.ToLowerInvariant();
        if (key.StartsWith("minecraft:", StringComparison.Ordinal)) key = key["minecraft:".Length..];
        return key switch
        {
            "overworld" => "overworld",
            "the_nether" => "the_nether",
            "the_end" => "the_end",
            _ => "other",
        };
    }
}

/// <summary>Compiles one user's effective permissions into the native table's rules: the user's own rules at layer 0, then each group of the user's chain
/// at its layer (inherited groups included, with the membership contexts folded into the rule contexts). The result has unique (node, layer, context)
/// keys; a clash keeps the denial, which the resolver would have chosen anyway.</summary>
public static class Compiler
{
    /// <summary>The engine's limit per subject.</summary>
    public const int MaxRulesPerSubject = 4096;

    /// <summary>Compiles the rules of <paramref name="user"/> at <paramref name="now"/>. Rules whose world names the server does not have are skipped (they
    /// could never match); reasons go to <paramref name="notes"/>.</summary>
    public static List<NativeRule> Compile(StoreDoc doc, Uuid user, long now, ContextMap map, List<string> notes)
    {
        Plan plan = Resolver.BuildPlan(doc, user, now);
        notes.AddRange(plan.Notes);
        var keyed = new Dictionary<(string Node, ushort Layer, ulong Context), NativeRule>();
        foreach (Entry<RuleDoc> e in Resolver.Collect<RuleDoc>(plan, now, u => u.Rules, g => g.Rules, r => r.Context, r => r.Expires))
        {
            byte[]? context = Encode(doc, map, e.Ctx, e.Item.Node, e.Source, notes);
            if (context is null) continue;
            var layer = (ushort)e.Layer;
            var key = (e.Item.Node, layer, Pack(context));
            var rule = new NativeRule(e.Item.Node, e.Item.Value, layer, context);
            if (keyed.TryGetValue(key, out NativeRule existing))
            {
                if (existing.Allow && !rule.Allow) keyed[key] = rule;  // same node, layer and context: the denial decides
                continue;
            }
            keyed.Add(key, rule);
        }

        var rules = keyed.Values.OrderBy(r => r.Layer).ToList();
        if (rules.Count > MaxRulesPerSubject)
        {
            notes.Add($"{user}: {rules.Count} rules compile for one subject; the deepest {rules.Count - MaxRulesPerSubject} are not sent (limit {MaxRulesPerSubject})");
            rules = rules.Take(MaxRulesPerSubject).ToList();
        }
        return rules;
    }

    private static byte[]? Encode(StoreDoc doc, ContextMap map, CtxPattern c, string node, string source, List<string> notes)
    {
        var bytes = new byte[6];
        Array.Fill(bytes, NativePermissionTable.Any);
        if (c.World is { } world)
        {
            if (!map.TryWorld(world, out byte index))
            {
                notes.Add($"rule '{node}' ({source}): world '{world}' is not a world of this server; the rule is skipped");
                return null;
            }
            bytes[0] = index;
        }
        if (c.Dimension is not null) bytes[1] = ContextMap.Dimension(c.Dimension);
        if (c.Gamemode is not null) bytes[2] = ContextMap.GameMode(c.Gamemode);
        if (c.Server is { } server)
        {
            if (server < 0 || server > 254) return null;
            bytes[3] = (byte)server;
        }
        if (c.Slot0 is { } slot0)
        {
            int at = SlotIndex(doc, 0, slot0);
            if (at < 0)
            {
                notes.Add($"rule '{node}' ({source}): slot value '{slot0}' is not declared; the rule is skipped");
                return null;
            }
            bytes[4] = (byte)at;
        }
        if (c.Slot1 is { } slot1)
        {
            int at = SlotIndex(doc, 1, slot1);
            if (at < 0)
            {
                notes.Add($"rule '{node}' ({source}): slot value '{slot1}' is not declared; the rule is skipped");
                return null;
            }
            bytes[5] = (byte)at;
        }
        return bytes;
    }

    private static int SlotIndex(StoreDoc doc, int slot, string value)
        => doc.Slots.Count > slot ? doc.Slots[slot].Values.IndexOf(value) : -1;

    private static ulong Pack(byte[] context)
    {
        ulong packed = 0;
        foreach (byte b in context) packed = (packed << 8) | b;
        return packed;
    }
}
