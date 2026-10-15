using System;
using System.Collections.Generic;
using System.Linq;
using Charter.Core;
using Charter.Model;
using Mc.Scripting;
using Mc.Scripting.Permissions;

namespace Charter.Tests;

/// <summary>Resolver tests. The expectations come from charter/DESIGN.md "Resolution (D3) and layers", not from the resolver's code.
/// Layers: 0 = the user's rules, then the groups in order (own groups by weight, parents after children), then default.</summary>
internal static class ResolverTests
{
    private const long Now = 1_700_000_000L;
    private static readonly Uuid Player = new(0x1111_2222_3333_4444UL, 0x5555_6666_7777_8888UL);
    private static readonly QueryContext Survival = new("w1", "overworld", "survival", 0, null, null);

    public static void All()
    {
        Program.Run("resolver: a user's own rules beat its groups", UserLayerBeatsGroups);
        Program.Run("resolver: higher weight beats lower weight", HigherWeightWins);
        Program.Run("resolver: parents follow children, weight does not lift them", ParentsAfterChildren);
        Program.Run("resolver: default is evaluated last", DefaultLast);
        Program.Run("resolver: inheritance cycles are tolerated", CyclesTolerated);
        Program.Run("resolver: context specificity before node specificity", ContextBeforeNodeSpecificity);
        Program.Run("resolver: exact > longest wildcard > star", NodeSpecificity);
        Program.Run("resolver: deny beats grant at equal specificity", DenyBeatsGrant);
        Program.Run("resolver: expired rules and memberships are ignored", ExpiredIgnored);
        Program.Run("resolver: unknown query positions match only any", UnknownPositions);
    }

    private static GroupDoc Group(int weight, string[]? parents, params RuleDoc[] rules) => new()
    {
        Weight = weight,
        Parents = (parents ?? Array.Empty<string>()).Select(p => new Membership { Group = p }).ToList(),
        Rules = rules.ToList(),
    };

    private static RuleDoc Rule(string node, bool value = true, ContextDoc? context = null, long? expires = null) =>
        new() { Node = node, Value = value, Context = context, Expires = expires };

    private static ContextDoc Ctx(string? world = null, string? gamemode = null, int? server = null) =>
        new() { World = world, Gamemode = gamemode, Server = server };

    private static StoreDoc Store(string[] memberOf, RuleDoc[] userRules, params (string Name, GroupDoc Group)[] groups)
    {
        var doc = new StoreDoc();
        doc.Groups["default"] = new GroupDoc();
        foreach ((string name, GroupDoc group) in groups) doc.Groups[name] = group;
        doc.Users[Player.ToString()] = new UserDoc
        {
            Groups = memberOf.Select(g => new Membership { Group = g }).ToList(),
            Rules = userRules.ToList(),
        };
        return doc;
    }

    private static Resolution Resolve(StoreDoc doc, string node, QueryContext? ctx = null) =>
        Resolver.Resolve(doc, Player, node, ctx ?? Survival, Now);

    private static IReadOnlyList<string> Order(StoreDoc doc) => Resolver.EffectiveGroups(doc, Player, Survival, Now);

    private static void UserLayerBeatsGroups()
    {
        StoreDoc doc = Store(["g"], [Rule("a.b", false)], ("g", Group(10, null, Rule("a.b"))));
        Resolution r = Resolve(doc, "a.b");
        Program.Check(r.Value == PermissionValue.False, "the user's denial beats the group's grant");
        Program.Check(r.Layer == 0 && r.Source == "user", "the decision is in layer 0, from the user");
        Program.Check(r.Rule is not null && r.Rule.Node == "a.b", "the deciding rule is reported");
    }

    private static void HigherWeightWins()
    {
        StoreDoc doc = Store(["lo", "hi"], [], ("lo", Group(5, null, Rule("x"))), ("hi", Group(10, null, Rule("x", false))));
        Resolution r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.False && r.Layer == 1 && r.Source == "hi", "the higher weight group decides");

        doc = Store(["lo", "hi"], [], ("lo", Group(10, null, Rule("x"))), ("hi", Group(5, null, Rule("x", false))));
        r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.True && r.Source == "lo", "swapping the weights swaps the answer");
    }

    private static void ParentsAfterChildren()
    {
        StoreDoc doc = Store(["child"], [], ("child", Group(1, ["parent"], Rule("x"))), ("parent", Group(100, null, Rule("x", false))));
        Program.Check(Order(doc).SequenceEqual(new[] { "child", "parent", "default" }), "the order is child, its parent, then default");
        Resolution r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.True && r.Layer == 1 && r.Source == "child",
            "the child's grant decides, although the parent weighs more");
    }

    private static void DefaultLast()
    {
        StoreDoc doc = Store(["g"], [], ("default", Group(1000, null, Rule("y", false))), ("g", Group(0, null, Rule("y"))));
        Resolution r = Resolve(doc, "y");
        Program.Check(r.Value == PermissionValue.True && r.Source == "g" && r.Layer == 1, "a group beats default, whatever default's weight");
        Program.Check(Order(doc).Last() == "default", "default is last in the order");

        StoreDoc alone = Store([], [], ("default", Group(1000, null, Rule("y", false))));
        r = Resolve(alone, "y");
        Program.Check(r.Value == PermissionValue.False && r.Source == "default" && r.Layer == 1, "a user in no group gets default");
    }

    private static void CyclesTolerated()
    {
        StoreDoc doc = Store(["a"], [], ("a", Group(1, ["b"], Rule("q"))), ("b", Group(2, ["a"], Rule("q", false))));
        IReadOnlyList<string> order = Order(doc);
        Program.Check(order.Count(g => g == "a") == 1 && order.Count(g => g == "b") == 1, "each group appears once in the order");
        Program.Check(order.Last() == "default", "default is still last");
        Resolution r = Resolve(doc, "q");
        Program.Check(r.Value == PermissionValue.True && r.Source == "a", "the first group in the order decides");
    }

    private static void ContextBeforeNodeSpecificity()
    {
        // A wildcard bound to the world beats an exact node that is not bound to any context.
        StoreDoc doc = Store(["g"], [], ("g", Group(0, null, Rule("a.*", false, Ctx(world: "w1")), Rule("a.b.c"))));
        Resolution r = Resolve(doc, "a.b.c");
        Program.Check(r.Value == PermissionValue.False && r.Source == "g", "a world-bound wildcard denial beats an unbound exact grant");

        var otherWorld = new QueryContext("w2", "overworld", "survival", 0, null, null);
        r = Resolve(doc, "a.b.c", otherWorld);
        Program.Check(r.Value == PermissionValue.True, "in another world the exact grant applies");
    }

    private static void NodeSpecificity()
    {
        StoreDoc doc = Store(["g"], [], ("g", Group(0, null, Rule("a.*", false), Rule("a.b.c"))));
        Program.Check(Resolve(doc, "a.b.c").Value == PermissionValue.True, "an exact node beats a wildcard");
        Program.Check(Resolve(doc, "a.b.d").Value == PermissionValue.False, "the wildcard covers the other nodes");

        doc = Store(["g"], [], ("g", Group(0, null, Rule("a.*"), Rule("a.b.*", false))));
        Program.Check(Resolve(doc, "a.b.c").Value == PermissionValue.False, "a longer wildcard beats a shorter one");
        Program.Check(Resolve(doc, "a.c").Value == PermissionValue.True, "the shorter wildcard applies outside the longer one");

        doc = Store(["g"], [], ("g", Group(0, null, Rule("*", false), Rule("a.*"))));
        Program.Check(Resolve(doc, "q").Value == PermissionValue.False, "star applies when nothing else matches");
        Program.Check(Resolve(doc, "a.x").Value == PermissionValue.True, "a.* beats star");
    }

    private static void DenyBeatsGrant()
    {
        StoreDoc doc = Store(["g"], [], ("g", Group(0, null, Rule("x"), Rule("x", false))));
        Program.Check(Resolve(doc, "x").Value == PermissionValue.False, "same layer, same specificity: the denial wins");

        doc = Store(["g"], [], ("g", Group(0, null, Rule("x", true, Ctx(world: "w1")), Rule("x", false, Ctx(gamemode: "survival")))));
        Program.Check(Resolve(doc, "x").Value == PermissionValue.False, "two matching contexts of equal specificity: the denial wins");
    }

    private static void ExpiredIgnored()
    {
        StoreDoc doc = Store(["g"], [Rule("x", false, null, Now - 100)], ("g", Group(0, null, Rule("x"))));
        Resolution r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.True && r.Layer == 1 && r.Source == "g", "an expired user denial is ignored");

        doc = Store(["g"], [Rule("x", false, null, Now + 100)], ("g", Group(0, null, Rule("x"))));
        r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.False && r.Layer == 0, "an unexpired user denial applies");

        doc = Store(["g"], [], ("g", Group(0, null, Rule("x", true, null, Now - 100))));
        r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.Unset && r.Layer == -1 && r.Source is null && r.Rule is null,
            "an expired rule in a group leaves no decision");

        doc = Store(["g"], [], ("g", Group(0, null, Rule("x"))));
        doc.Users[Player.ToString()].Groups[0].Expires = Now - 100;
        r = Resolve(doc, "x");
        Program.Check(r.Value == PermissionValue.Unset, "an expired membership does not give the group's rules");
        Program.Check(!Order(doc).Contains("g"), "and the group is not in the effective order");
    }

    private static void UnknownPositions()
    {
        StoreDoc doc = Store([], [Rule("x", true, Ctx(world: "w1"))]);
        Program.Check(Resolve(doc, "x", QueryContext.Unknown).Value == PermissionValue.Unset, "an unknown world does not match a world rule");
        Program.Check(Resolve(doc, "x", new QueryContext("w1", null, null, null, null, null)).Value == PermissionValue.True,
            "a known world matches it, the other positions being unknown");

        doc = Store([], [Rule("x", true, Ctx(gamemode: "creative"))]);
        Program.Check(Resolve(doc, "x").Value == PermissionValue.Unset, "a gamemode rule does not match another gamemode");
        Program.Check(Resolve(doc, "x", new QueryContext("w1", "overworld", null, 0, null, null)).Value == PermissionValue.Unset,
            "an unknown gamemode does not match a gamemode rule");

        doc = Store([], [Rule("x", true, Ctx(server: 3))]);
        Program.Check(Resolve(doc, "x").Value == PermissionValue.Unset, "a server rule does not match another server");

        doc = Store([], [Rule("x")]);
        Program.Check(Resolve(doc, "x", QueryContext.Unknown).Value == PermissionValue.True, "a rule with no context matches an unknown query");
    }
}
