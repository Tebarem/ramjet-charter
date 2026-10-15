using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Charter.Model;
using Mc.Scripting;

namespace Charter.Import;

/// <summary>Imports Ramjet's built-in permissions.json (FilePermissionProvider's format) into a store document.
/// Groups and users in the file replace same-named ones in the store (each replacement is reported). Group names are lower-cased and reported.
/// "-node" is a denial. prefix and suffix become affixes whose priority is the group's weight. The implicit default membership and inheritance
/// of default are dropped (the store always evaluates default last). Unknown keys are kept under Extra and reported.</summary>
public static class PermissionsJsonImport
{
    /// <summary>Reads <paramref name="path"/> and merges it into a copy of <paramref name="current"/>.</summary>
    /// <exception cref="InvalidDataException">The file is larger than 16 MiB, is not JSON, or its top level is not an object.</exception>
    public static ImportReport Run(string path, StoreDoc current)
    {
        var report = new ImportReport();
        StoreDoc result = ImportSupport.DeepCopy(current);
        report.Result = result;

        byte[] bytes = ImportSupport.ReadBounded(path, ImportLimits.MaxRamjetFileBytes);
        using JsonDocument json = ImportSupport.ParseOrThrow(bytes, path);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: the top level is not an object");

        Dictionary<string, JsonElement>? extra = result.Extra;
        int groups = 0;
        int users = 0;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "version":
                    if (!p.Value.TryGetInt32(out int version) || version != 1)
                        report.AddNote($"{path}: version {p.Value.GetRawText()} is read with the version 1 layout");
                    break;
                case "groups":
                    groups = ImportGroups(p.Value, report, result);
                    break;
                case "users":
                    users = ImportUsers(p.Value, report, result);
                    break;
                default:
                    ImportSupport.KeepUnknown(ref extra, p, report, "top level");
                    break;
            }
        }
        result.Extra = extra;

        ImportSupport.EnsureDefault(result);
        ImportSupport.ReportDanglingMemberships(result, report);
        report.AddNote($"{path}: {groups} groups and {users} users read");
        return report;
    }

    private static int ImportGroups(JsonElement groups, ImportReport report, StoreDoc result)
    {
        if (groups.ValueKind != JsonValueKind.Object)
        {
            report.AddSkipped("groups: not an object, no groups read");
            return 0;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int written = 0;
        foreach (JsonProperty p in groups.EnumerateObject())
        {
            string name = ImportSupport.LowerName(p.Name);
            if (name != p.Name) report.AddRemapped($"group '{p.Name}' -> '{name}'");
            if (seen.Contains(name))
            {
                report.AddSkipped($"group '{p.Name}': same name as an earlier group after lower-casing; the earlier one is kept");
                continue;
            }
            if (seen.Count >= ImportLimits.MaxGroups)
            {
                report.AddSkipped($"groups: more than {ImportLimits.MaxGroups}; the rest are skipped");
                break;
            }
            seen.Add(name);
            if (p.Value.ValueKind != JsonValueKind.Object)
            {
                report.AddSkipped($"group '{name}': not an object");
                continue;
            }
            GroupDoc group = ReadGroup(name, p.Value, report);
            if (result.Groups.ContainsKey(name)) report.AddNote($"group '{name}' replaces the one in the store");
            result.Groups[name] = group;
            written++;
        }
        return written;
    }

    private static GroupDoc ReadGroup(string name, JsonElement obj, ImportReport report)
    {
        var group = new GroupDoc();
        string prefix = "";
        string suffix = "";
        Dictionary<string, JsonElement>? extra = null;
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            switch (p.Name)
            {
                case "weight":
                    if (p.Value.TryGetInt32(out int weight)) group.Weight = weight;
                    else report.AddSkipped($"group '{name}': weight {p.Value.GetRawText()} is not an integer; 0 is used");
                    break;
                case "inherits":
                    foreach (string parent in ImportSupport.StringList(p.Value, report, $"group '{name}': inherits"))
                    {
                        string parentName = ImportSupport.LowerName(parent);
                        if (parentName != parent) report.AddRemapped($"group '{name}' inherits '{parent}' -> '{parentName}'");
                        if (parentName == ImportSupport.DefaultGroup)
                        {
                            report.AddNote($"group '{name}': inheriting default is implicit and dropped");
                            continue;
                        }
                        if (!group.Parents.Exists(m => m.Group == parentName)) group.Parents.Add(new Membership { Group = parentName });
                    }
                    break;
                case "prefix":
                    if (p.Value.ValueKind == JsonValueKind.String) prefix = p.Value.GetString() ?? "";
                    else report.AddSkipped($"group '{name}': prefix is not a string");
                    break;
                case "suffix":
                    if (p.Value.ValueKind == JsonValueKind.String) suffix = p.Value.GetString() ?? "";
                    else report.AddSkipped($"group '{name}': suffix is not a string");
                    break;
                case "permissions":
                    ReadPermissions(p.Value, $"group '{name}'", group.Rules, report);
                    break;
                default:
                    ImportSupport.KeepUnknown(ref extra, p, report, $"group '{name}'");
                    break;
            }
        }
        if (prefix.Length > 0) group.Prefixes.Add(new AffixDoc { Text = prefix, Priority = group.Weight });
        if (suffix.Length > 0) group.Suffixes.Add(new AffixDoc { Text = suffix, Priority = group.Weight });
        group.Extra = extra;
        return group;
    }

    private static void ReadPermissions(JsonElement obj, string where, List<RuleDoc> rules, ImportReport report)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            report.AddSkipped($"{where}: permissions is not an object");
            return;
        }
        var byNode = new Dictionary<string, RuleDoc>(StringComparer.Ordinal);
        foreach (JsonProperty entry in obj.EnumerateObject())
        {
            if (entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                report.AddSkipped($"{where}: '{entry.Name}' is not true or false");
                continue;
            }
            bool negated = entry.Name.StartsWith('-');
            string node = negated ? entry.Name[1..] : entry.Name;
            if (node.Length == 0)
            {
                report.AddSkipped($"{where}: an empty node name");
                continue;
            }
            // As the engine reads it: "-node" is false whatever its value, and a later entry for the same node overrides an earlier one.
            bool value = !negated && entry.Value.GetBoolean();
            if (byNode.TryGetValue(node, out RuleDoc? existing))
            {
                existing.Value = value;
                report.AddNote($"{where}: '{node}' is given twice; the later entry wins");
                continue;
            }
            if (rules.Count >= ImportLimits.MaxRulesPerSubject)
            {
                report.AddSkipped($"{where}: more than {ImportLimits.MaxRulesPerSubject} permissions; the rest are dropped");
                break;
            }
            var rule = new RuleDoc { Node = node, Value = value };
            rules.Add(rule);
            byNode[node] = rule;
        }
    }

    private static int ImportUsers(JsonElement users, ImportReport report, StoreDoc result)
    {
        if (users.ValueKind != JsonValueKind.Object)
        {
            report.AddSkipped("users: not an object, no users read");
            return 0;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int written = 0;
        foreach (JsonProperty p in users.EnumerateObject())
        {
            if (!ImportSupport.TryNormalizeUuid(p.Name, out string id))
            {
                report.AddSkipped($"user '{p.Name}': the key is not a uuid (the store keys users by uuid)");
                continue;
            }
            if (id != p.Name) report.AddRemapped($"user '{p.Name}' -> '{id}'");
            if (seen.Contains(id))
            {
                report.AddSkipped($"user '{p.Name}': same uuid as an earlier entry; the earlier one is kept");
                continue;
            }
            if (seen.Count >= ImportLimits.MaxUsers)
            {
                report.AddSkipped($"users: more than {ImportLimits.MaxUsers}; the rest are skipped");
                break;
            }
            seen.Add(id);
            if (p.Value.ValueKind != JsonValueKind.Object)
            {
                report.AddSkipped($"user '{id}': not an object");
                continue;
            }
            UserDoc user = ReadUser(id, p.Value, report);
            if (result.Users.ContainsKey(id)) report.AddNote($"user '{id}' replaces the one in the store");
            result.Users[id] = user;
            written++;
        }
        return written;
    }

    private static UserDoc ReadUser(string id, JsonElement obj, ImportReport report)
    {
        var user = new UserDoc();
        string where = $"user '{id}'";
        Dictionary<string, JsonElement>? extra = null;
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name":
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        string? text = p.Value.GetString();
                        user.Name = string.IsNullOrEmpty(text) ? null : text;
                    }
                    else report.AddSkipped($"{where}: name is not a string");
                    break;
                case "groups":
                    foreach (string member in ImportSupport.StringList(p.Value, report, $"{where}: groups"))
                    {
                        string group = ImportSupport.LowerName(member);
                        if (group != member) report.AddRemapped($"{where} is in '{member}' -> '{group}'");
                        if (group == ImportSupport.DefaultGroup)
                        {
                            report.AddNote($"{where}: membership in default is implicit and dropped");
                            continue;
                        }
                        if (!user.Groups.Exists(m => m.Group == group)) user.Groups.Add(new Membership { Group = group });
                    }
                    break;
                case "permissions":
                    ReadPermissions(p.Value, where, user.Rules, report);
                    break;
                default:
                    ImportSupport.KeepUnknown(ref extra, p, report, where);
                    break;
            }
        }
        user.Extra = extra;
        return user;
    }
}
