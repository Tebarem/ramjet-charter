using Mc.Scripting;
using System.Globalization;
using Charter.Model;

namespace Charter.Commands;

/// <summary>The rule tail of a command: <c>&lt;node&gt; [true|false] [key=value ...] [for &lt;duration&gt;]</c>.</summary>
/// <param name="Node">The permission node (validated).</param>
/// <param name="Value">The explicit value, null when none was typed.</param>
/// <param name="Context">The context pattern, null for "any".</param>
/// <param name="Seconds">The duration in seconds from "for", null for permanent.</param>
internal sealed record RuleTail(string Node, bool? Value, ContextDoc? Context, long? Seconds);

/// <summary>Parsing and formatting for the command tree. Pure (no server types), so the rules are easy to read and test.</summary>
internal static class Tokens
{
    /// <summary>Longest accepted duration (100 years); anything longer is a typo.</summary>
    public const long MaxDurationSeconds = 100L * 365 * 86400;

    private const int MaxNodeLength = 256;

    public static string[] Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Group, track and slot-value names: lower-case letters, digits, '_' and '-', 1 to 32 characters.</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 32) return false;
        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
        }
        return true;
    }

    /// <summary>Permission nodes: dot-separated segments of letters, digits, '_' and '-'; a trailing "*" segment or a lone "*" is a wildcard.
    /// No regex (D12): wildcards are only allowed as the last segment.</summary>
    public static bool IsValidNode(string node)
    {
        if (node.Length is 0 or > MaxNodeLength) return false;
        if (node == "*") return true;
        string[] parts = node.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) return false;
            if (part == "*")
            {
                if (i != parts.Length - 1) return false;
                continue;
            }
            foreach (char c in part)
            {
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
            }
        }
        return true;
    }

    /// <summary>Meta keys: letters, digits, '_', '.', '-', ':' (1 to 64 characters).</summary>
    public static bool IsValidMetaKey(string key)
    {
        if (key.Length is 0 or > 64) return false;
        foreach (char c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ':')) return false;
        }
        return true;
    }

    /// <summary>Parses "30m", "2h", "7d", "1w2d", "90s". Units: s, m, h, d, w. Returns the total in seconds (greater than zero).</summary>
    public static bool TryParseDuration(string text, out long seconds)
    {
        seconds = 0;
        long total = 0;
        long current = -1;
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);
            if (char.IsAsciiDigit(c))
            {
                current = (current < 0 ? 0 : current) * 10 + (c - '0');
                if (current > MaxDurationSeconds) return false;
                continue;
            }
            if (current < 0) return false;
            long unit = c switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                'd' => 86400,
                'w' => 604800,
                _ => 0,
            };
            if (unit == 0) return false;
            total += current * unit;
            current = -1;
            if (total > MaxDurationSeconds) return false;
        }
        if (current >= 0 || total <= 0) return false;
        seconds = total;
        return true;
    }

    /// <summary>Applies one <c>key=value</c> token to a context pattern. Keys: world, dimension, gamemode, server, or a declared slot key.</summary>
    public static bool TryApplyContext(string token, IReadOnlyList<ContextSlotDoc> slots, ContextDoc into, out string? error)
    {
        error = null;
        int eq = token.IndexOf('=');
        if (eq <= 0 || eq == token.Length - 1)
        {
            error = $"expected key=value, got '{token}'";
            return false;
        }
        string key = token[..eq].ToLowerInvariant();
        string value = token[(eq + 1)..];
        switch (key)
        {
            case "world":
                if (into.World is not null) return Duplicate(key, out error);
                into.World = value;
                return true;
            case "dimension":
                if (into.Dimension is not null) return Duplicate(key, out error);
                string? dimension = value.ToLowerInvariant() switch
                {
                    "overworld" => "overworld",
                    "nether" or "the_nether" => "the_nether",
                    "end" or "the_end" => "the_end",
                    "other" => "other",
                    _ => null,
                };
                if (dimension is null)
                {
                    error = $"unknown dimension '{value}' (overworld, nether, end, other)";
                    return false;
                }
                into.Dimension = dimension;
                return true;
            case "gamemode":
                if (into.Gamemode is not null) return Duplicate(key, out error);
                string gamemode = value.ToLowerInvariant();
                if (gamemode is not ("survival" or "creative" or "adventure" or "spectator"))
                {
                    error = $"unknown gamemode '{value}' (survival, creative, adventure, spectator)";
                    return false;
                }
                into.Gamemode = gamemode;
                return true;
            case "server":
                if (into.Server is not null) return Duplicate(key, out error);
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int server) || server is < 0 or > 255)
                {
                    error = $"server must be 0-255, got '{value}'";
                    return false;
                }
                into.Server = server;
                return true;
        }
        for (int slot = 0; slot < slots.Count && slot < 2; slot++)
        {
            if (!string.Equals(slots[slot].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (slot == 0 ? into.Slot0 is not null : into.Slot1 is not null) return Duplicate(key, out error);
            if (!slots[slot].Values.Contains(value))
            {
                error = $"'{value}' is not a value of {key} (values: {string.Join(", ", slots[slot].Values)})";
                return false;
            }
            if (slot == 0) into.Slot0 = value;
            else into.Slot1 = value;
            return true;
        }
        string declared = string.Join(", ", slots.Select(s => s.Key));
        error = declared.Length == 0
            ? $"unknown context key '{key}' (world, dimension, gamemode, server)"
            : $"unknown context key '{key}' (world, dimension, gamemode, server, {declared})";
        return false;
    }

    private static bool Duplicate(string key, out string? error)
    {
        error = $"context key '{key}' is given twice";
        return false;
    }

    /// <summary>Parses the tokens after a node: an optional value (only when <paramref name="allowValue"/>), context pairs and an optional
    /// <c>for &lt;duration&gt;</c> (only when <paramref name="allowFor"/>). Returns null and an error text when the text is not valid.</summary>
    public static RuleTail? ParseRuleText(string text, bool allowValue, bool allowFor, IReadOnlyList<ContextSlotDoc> slots, out string? error)
    {
        error = null;
        string[] tokens = Split(text);
        if (tokens.Length == 0)
        {
            error = "a permission node is required";
            return null;
        }
        string node = tokens[0];
        if (!IsValidNode(node))
        {
            error = $"'{node}' is not a permission node (letters, digits, '_', '-', dot-separated; '*' only as the last segment)";
            return null;
        }
        bool? value = null;
        var context = new ContextDoc();
        long? seconds = null;
        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (i == 1 && allowValue && token is "true" or "false")
            {
                value = token == "true";
                continue;
            }
            if (token == "for")
            {
                if (!allowFor || i + 1 >= tokens.Length || i + 2 != tokens.Length)
                {
                    error = allowFor ? "'for' takes one duration at the end, for example: for 7d" : $"unexpected '{token}'";
                    return null;
                }
                if (!TryParseDuration(tokens[i + 1], out long s))
                {
                    error = $"'{tokens[i + 1]}' is not a duration (examples: 30m, 2h, 7d, 1w2d)";
                    return null;
                }
                seconds = s;
                break;
            }
            if (!TryApplyContext(token, slots, context, out error)) return null;
        }
        return new RuleTail(node, value, context.IsAny ? null : context, seconds);
    }

    /// <summary>Resolves a relative file name below <paramref name="baseDir"/>. Rejects absolute paths, drive letters, ':' and any ".." segment,
    /// and checks that the result stays inside the base (the plugin may only touch its own data folder).</summary>
    public static bool TryResolveInside(string baseDir, string relative, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;
        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "a file name is required";
            return false;
        }
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\'))
        {
            error = "absolute paths are not allowed; give a name relative to the folder";
            return false;
        }
        foreach (string segment in relative.Split('/', '\\'))
        {
            if (segment == "..")
            {
                error = "'..' is not allowed in a file name";
                return false;
            }
        }
        string root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = "the file must stay inside its folder";
            return false;
        }
        fullPath = full;
        return true;
    }

    /// <summary>Compact duration text: "1w 2d 3h 4m 5s" (zero units left out).</summary>
    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "0s";
        var parts = new List<string>(5);
        long[] units = [604800, 86400, 3600, 60, 1];
        string[] names = ["w", "d", "h", "m", "s"];
        for (int i = 0; i < units.Length; i++)
        {
            long n = seconds / units[i];
            if (n == 0) continue;
            parts.Add($"{n}{names[i]}");
            seconds -= n * units[i];
        }
        return string.Join(" ", parts);
    }

    /// <summary>"permanent", "expires in 2h 3m" or "expired", relative to <paramref name="now"/>.</summary>
    public static string DescribeExpiry(long? expires, long now)
    {
        if (expires is not { } e) return "permanent";
        return e > now ? $"expires in {FormatDuration(e - now)}" : "expired";
    }

    /// <summary>UTC wall-clock text for audit and info output.</summary>
    public static string FormatTime(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>"[world=x dimension=y]" for a pattern, empty when it matches anywhere.</summary>
    public static string FormatContext(ContextDoc? context)
    {
        if (context is null || context.IsAny) return "";
        var parts = new List<string>(6);
        if (context.World is not null) parts.Add($"world={context.World}");
        if (context.Dimension is not null) parts.Add($"dimension={context.Dimension}");
        if (context.Gamemode is not null) parts.Add($"gamemode={context.Gamemode}");
        if (context.Server is not null) parts.Add($"server={context.Server}");
        if (context.Slot0 is not null) parts.Add($"slot0={context.Slot0}");
        if (context.Slot1 is not null) parts.Add($"slot1={context.Slot1}");
        return " [" + string.Join(" ", parts) + "]";
    }

    public static string FormatRule(RuleDoc rule, long now)
        => $"{rule.Node} = {(rule.Value ? "true" : "false")}{FormatContext(rule.Context)} ({DescribeExpiry(rule.Expires, now)})";

    /// <summary>A membership or meta entry is in force when it has no expiry or the expiry is in the future.</summary>
    public static bool Active(long? expires, long now) => expires is not { } e || e > now;
}
