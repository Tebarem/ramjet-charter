using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Charter.Import;
using Charter.Model;
using Mc.Scripting;

namespace Charter.Tests;

/// <summary>Golden tests for the two importers over the fixtures in fixtures/. Expectations are written from the formats (charter/docs/import.md).</summary>
internal static class ImportTests
{
    private const long Now = 1_700_000_000L;
    private const long FarFuture = 4_102_444_800L;
    private const string Notch = "069a79f4-44e9-4726-a5be-fca90e38aaf5";
    private const string Steve = "9f9a1d7a-0000-4000-8000-000000000001";

    public static void All()
    {
        Program.Run("ramjet: groups, weights, parents, affixes, rules", RamjetGolden);
        Program.Run("ramjet: the input store is not modified", RamjetInputUntouched);
        Program.Run("ramjet: file over 16 MiB refused", RamjetTooLarge);
        Program.Run("ramjet: malformed or non-object JSON refused", RamjetMalformed);
        Program.Run("luckperms: groups, weights, parents, affixes, meta, display name, tracks", LpGroupsAndTracks);
        Program.Run("luckperms: temporary, regex and negated entries", LpTemporaryAndRegex);
        Program.Run("luckperms: contexts map or are skipped", LpContexts);
        Program.Run("luckperms: users, primary group, users with nothing beyond default", LpUsers);
        Program.Run("luckperms: gzip and plain give the same store", LpGzipMatchesPlain);
        Program.Run("luckperms: decompressed size over 64 MiB refused", LpTooLarge);
        Program.Run("luckperms: malformed JSON refused", LpMalformed);
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string Scratch(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "charter-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static StoreDoc CurrentStore()
    {
        var store = new StoreDoc();
        store.Groups["default"] = new GroupDoc();
        store.Groups["admin"] = new GroupDoc { Weight = 1, Rules = [new RuleDoc { Node = "old.node" }] };
        store.Groups["keep"] = new GroupDoc { Weight = 3 };
        store.Users[Notch] = new UserDoc { Name = "Old" };
        return store;
    }

    private static bool Has(List<RuleDoc> rules, string node, bool value) =>
        rules.Any(r => r.Node == node && r.Value == value && r.Context is null && r.Expires is null);

    private static ImportReport LpRun() => LuckPermsImport.RunAt(Fixture("luckperms-export.json"), new StoreDoc(), Now);

    // ---- Ramjet permissions.json ----

    private static void RamjetGolden()
    {
        ImportReport r = PermissionsJsonImport.Run(Fixture("permissions.json"), CurrentStore());
        StoreDoc d = r.Result;

        Program.Check(d.Groups.ContainsKey("mod") && !d.Groups.ContainsKey("Mod"), "group Mod is stored as mod");
        Program.Check(r.Remapped.Any(s => s.Contains("'Mod' -> 'mod'")), "the Mod rename is reported");
        Program.Check(d.Groups.ContainsKey("builder") && d.Groups.ContainsKey("keep") && d.Groups.ContainsKey("default"),
            "builder read, keep (not in the file) kept, default present");

        GroupDoc admin = d.Groups["admin"];
        Program.Check(admin.Weight == 100, "admin weight is 100");
        Program.Check(admin.Parents.Count == 1 && admin.Parents[0].Group == "mod", "admin inherits mod (remapped from Mod)");
        Program.Check(admin.Rules.Count == 1 && Has(admin.Rules, "*", true), "admin's rules are the file's; the store's old rule is replaced");
        Program.Check(r.Notes.Any(s => s.Contains("'admin' replaces")), "the admin replacement is reported");

        GroupDoc mod = d.Groups["mod"];
        Program.Check(mod.Weight == 50, "mod weight is 50");
        Program.Check(mod.Parents.Count == 0, "inheriting default is dropped (implicit)");
        Program.Check(mod.Prefixes.Count == 1 && mod.Prefixes[0].Text == "&c[Mod] " && mod.Prefixes[0].Priority == 50,
            "prefix becomes an affix whose priority is the weight");
        Program.Check(mod.Suffixes.Count == 0, "an empty suffix is not imported");
        Program.Check(Has(mod.Rules, "charter.admin.view", true), "a grant is kept");
        Program.Check(Has(mod.Rules, "minecraft.command.op", false), "-node is read as a denial");
        Program.Check(Has(mod.Rules, "homes.*", true), "a wildcard is kept");

        GroupDoc builder = d.Groups["builder"];
        Program.Check(builder.Extra is not null && builder.Extra.ContainsKey("unknownKey"), "an unknown group key is kept under extra");
        Program.Check(r.Notes.Any(s => s.Contains("unknownKey")), "an unknown group key is reported");

        Program.Check(d.Groups["default"].Rules.Count == 1 && Has(d.Groups["default"].Rules, "minecraft.command.help", true),
            "default is taken from the file");

        UserDoc user = d.Users[Notch];
        Program.Check(user.Name == "Notch", "the user name is read");
        Program.Check(user.Groups.Count == 1 && user.Groups[0].Group == "admin", "membership ADMIN is lower-cased");
        Program.Check(Has(user.Rules, "minecraft.command.stop", false) && Has(user.Rules, "fly", true), "user rules are read");
        Program.Check(r.Remapped.Any(s => s.Contains(Notch)), "the upper-case uuid is normalised and reported");
        Program.Check(!d.Users.ContainsKey("NOTAUUID") && r.Skipped.Any(s => s.Contains("NOTAUUID")), "a non-uuid user key is skipped and reported");
        Program.Check(d.Extra is not null && d.Extra.ContainsKey("comment"), "an unknown top-level key is kept under extra");
        Program.Check(r.Skipped.Count == 1, "exactly one item is skipped in this fixture");
    }

    private static void RamjetInputUntouched()
    {
        StoreDoc current = CurrentStore();
        ImportReport r = PermissionsJsonImport.Run(Fixture("permissions.json"), current);
        Program.Check(!ReferenceEquals(r.Result, current), "the result is a new document");
        Program.Check(current.Groups.Count == 3 && current.Groups["admin"].Weight == 1 && current.Groups["admin"].Rules[0].Node == "old.node",
            "the input groups are unchanged");
        Program.Check(current.Users[Notch].Name == "Old", "the input users are unchanged");
    }

    private static void RamjetTooLarge()
    {
        string path = Scratch("ramjet-too-large.json");
        try
        {
            using (FileStream fs = File.Create(path))
            {
                fs.SetLength(ImportLimits.MaxRamjetFileBytes + 1);
            }
            Program.Expect<InvalidDataException>(() => PermissionsJsonImport.Run(path, CurrentStore()), "a file of 16 MiB + 1 byte is refused");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RamjetMalformed()
    {
        string path = Scratch("ramjet-bad.json");
        try
        {
            File.WriteAllText(path, "{ \"groups\": ");
            Program.Expect<InvalidDataException>(() => PermissionsJsonImport.Run(path, CurrentStore()), "truncated JSON is refused");
            File.WriteAllText(path, "[]");
            Program.Expect<InvalidDataException>(() => PermissionsJsonImport.Run(path, CurrentStore()), "a top-level array is refused");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- LuckPerms export ----

    private static void LpGroupsAndTracks()
    {
        ImportReport r = LpRun();
        StoreDoc d = r.Result;
        Program.Check(d.Groups.ContainsKey("default") && d.Groups.ContainsKey("member") && d.Groups.ContainsKey("admin"),
            "default, member and admin are present");

        GroupDoc member = d.Groups["member"];
        Program.Check(member.Weight == 10, "weight.10 is read");
        Program.Check(member.Parents.Count == 0, "group.default is dropped as implicit");
        Program.Check(member.Prefixes.Count == 1 && member.Prefixes[0].Text == "&7[Member] " && member.Prefixes[0].Priority == 20,
            "prefix.20 keeps its priority and its text");
        Program.Check(member.Meta.Count == 1 && member.Meta[0].Key == "rank" && member.Meta[0].Value == "member", "meta.rank.member is read");

        GroupDoc admin = d.Groups["admin"];
        Program.Check(admin.Weight == 100, "weight.100 is read");
        Program.Check(admin.Parents.Count == 1 && admin.Parents[0].Group == "member", "admin inherits member");
        Program.Check(admin.Suffixes.Count == 1 && admin.Suffixes[0].Text == "<3" && admin.Suffixes[0].Priority == 5, "suffix.5 is read");
        Program.Check(admin.Meta.Count == 1 && admin.Meta[0].Value == "Chief.Admin", "an escaped dot in meta is unescaped");
        Program.Check(admin.DisplayName == "Administrator", "displayname is read");
        Program.Check(Has(admin.Rules, "*", true), "a wildcard rule is read");

        Program.Check(d.Tracks.ContainsKey("staff") && d.Tracks["staff"].Groups.SequenceEqual(new[] { "member", "admin" }),
            "a track keeps only the groups in the store");
        Program.Check(r.Skipped.Any(s => s.Contains("ghost")), "a track group that is not in the store is reported");
    }

    private static void LpTemporaryAndRegex()
    {
        ImportReport r = LpRun();
        GroupDoc member = r.Result.Groups["member"];
        Program.Check(member.Rules.Any(x => x.Node == "essentials.kit.vip" && x.Expires == FarFuture), "a future expiry is kept");
        Program.Check(!member.Rules.Any(x => x.Node == "essentials.old"), "a past expiry is not imported");
        Program.Check(r.Skipped.Any(s => s.Contains("essentials.old") && s.Contains("expired")), "the expired entry is reported");
        Program.Check(!member.Rules.Any(x => x.Node.StartsWith("r=", StringComparison.Ordinal)), "no regex node becomes a rule");
        Program.Check(r.Skipped.Any(s => s.Contains("regex")), "the regex node is reported");
        Program.Check(member.Rules.Any(x => x.Node == "essentials.dim" && !x.Value && x.Context?.Dimension == "the_nether"),
            "a denial with a dimension-type context keeps the dimension");
        Program.Check(member.Rules.Any(x => x.Node == "essentials.kit" && x.Value && x.Context is null && x.Expires is null),
            "a plain node is a permanent, context-free rule");
    }

    private static void LpContexts()
    {
        ImportReport r = LpRun();
        List<RuleDoc> rules = r.Result.Groups["member"].Rules;
        Program.Check(rules.Any(x => x.Node == "essentials.fly" && x.Context?.World == "creative_world"), "a world context is kept");

        var worlds = rules.Where(x => x.Node == "essentials.world").Select(x => x.Context?.World).OrderBy(w => w).ToList();
        Program.Check(worlds.SequenceEqual(new string?[] { "a", "b" }), "a multi-value world gives one rule per value");

        Program.Check(!rules.Any(x => x.Node == "essentials.bad"), "an unknown context key: the entry is not imported");
        Program.Check(r.Skipped.Any(s => s.Contains("custom-key")), "an unknown context key is reported");
        Program.Check(!rules.Any(x => x.Node == "essentials.server"), "a non-numeric server: the entry is not imported");
        Program.Check(r.Skipped.Any(s => s.Contains("lobby")), "a non-numeric server is reported");
        Program.Check(rules.Any(x => x.Node == "essentials.server2" && x.Context?.Server == 3), "a numeric server id is kept");
        Program.Check(rules.Any(x => x.Node == "essentials.global" && x.Context is null), "server global means any server");
        Program.Check(rules.Any(x => x.Node == "essentials.gm" && x.Context?.Gamemode == "creative" && x.Context?.World == "w"),
            "two keys in one context are both kept");
    }

    private static void LpUsers()
    {
        ImportReport r = LpRun();
        StoreDoc d = r.Result;

        UserDoc steve = d.Users[Steve];
        Program.Check(steve.Name == "Steve", "the username is read");
        Program.Check(steve.Groups.Count(g => g.Group == "admin") == 1, "admin is a membership once (the primary group is not added twice)");
        Membership memberOfSteve = steve.Groups.Single(g => g.Group == "member");
        Program.Check(memberOfSteve.Context?.Server == 3 && memberOfSteve.Expires == FarFuture, "a temporary, server-bound membership is kept");
        Program.Check(steve.Rules.Any(x => x.Node == "essentials.god"), "a user rule is read");
        Program.Check(r.Remapped.Any(s => s.Contains(Steve)), "the upper-case uuid is normalised and reported");

        UserDoc alex = d.Users["11111111-2222-4333-8444-555555555555"];
        Program.Check(alex.Groups.Count == 1 && alex.Groups[0].Group == "member", "a primary group with no membership becomes one");
        Program.Check(r.Notes.Any(s => s.Contains("primary group 'member' imported")), "the primary group membership is reported");

        Program.Check(!d.Users.ContainsKey("00000000-0000-4000-8000-000000000001"), "a user with nothing beyond default is not imported");
        Program.Check(r.Notes.Any(s => s.Contains("nothing beyond default")), "and it is counted in the notes");
    }

    private static void LpGzipMatchesPlain()
    {
        string gz = Scratch("luckperms-export.json.gz");
        try
        {
            byte[] plain = File.ReadAllBytes(Fixture("luckperms-export.json"));
            using (FileStream fs = File.Create(gz))
            using (var z = new GZipStream(fs, CompressionLevel.Optimal))
            {
                z.Write(plain, 0, plain.Length);
            }
            ImportReport fromPlain = LpRun();
            ImportReport fromGz = LuckPermsImport.RunAt(gz, new StoreDoc(), Now);
            string a = JsonSerializer.Serialize(fromPlain.Result, StoreJson.Default.StoreDoc);
            string b = JsonSerializer.Serialize(fromGz.Result, StoreJson.Default.StoreDoc);
            Program.Check(a == b, "the gzipped export gives the same store as the plain one");
            Program.Check(fromPlain.SkippedCount == fromGz.SkippedCount && fromPlain.NotesCount == fromGz.NotesCount,
                "and the same skipped and note counts");
        }
        finally
        {
            File.Delete(gz);
        }
    }

    private static void LpTooLarge()
    {
        string path = Scratch("luckperms-too-large.json.gz");
        try
        {
            using (FileStream fs = File.Create(path))
            using (var z = new GZipStream(fs, CompressionLevel.Fastest))
            {
                z.WriteByte((byte)'{');
                var block = new byte[1 << 20];
                Array.Fill(block, (byte)' ');
                for (long written = 0; written <= ImportLimits.MaxLuckPermsJsonBytes; written += block.Length) z.Write(block, 0, block.Length);
                z.WriteByte((byte)'}');
            }
            Program.Expect<InvalidDataException>(() => LuckPermsImport.RunAt(path, new StoreDoc(), Now),
                "an export that decompresses to more than 64 MiB is refused");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void LpMalformed()
    {
        string path = Scratch("luckperms-bad.json");
        try
        {
            File.WriteAllText(path, "{ \"groups\": [");
            Program.Expect<InvalidDataException>(() => LuckPermsImport.RunAt(path, new StoreDoc(), Now), "truncated JSON is refused");
            File.WriteAllText(path, "[1, 2]");
            Program.Expect<InvalidDataException>(() => LuckPermsImport.RunAt(path, new StoreDoc(), Now), "a top-level array is refused");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
