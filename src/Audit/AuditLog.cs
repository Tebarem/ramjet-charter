using Mc.Scripting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charter.Core;

namespace Charter.Audit;

/// <summary>One audit record as shown to readers (<c>/charter audit</c>).</summary>
public sealed record AuditRecordView(long Seq, long Time, string Actor, string Action, string? Subject, string? Detail);

// Append-only, hash-chained audit log. One JSON object per line in <data>/audit/audit-NNNN.jsonl, rotated at 4 MiB, never deleted.
// Record layout (keys in this order, no spaces):
//   {"seq":N,"time":T,"actor":{"kind":K,"name":S,"uuid":U|null},"action":A,"subject":S|null,"detail":D|null,"prev":H,"hash":H}
// prev = SHA-256 (lower hex) of the previous line's exact bytes, "0"*64 for the first record.
// hash = SHA-256 of the canonical text: the same line without the trailing ,"hash":"..." field (the line minus its last 75 characters, plus "}").
// Because the writer always puts "hash" last and escapes all non-ASCII, the line is pure ASCII and its length is its byte length.
public sealed class AuditLog : IAuditSink
{
    public const long MaxFileBytes = 4L * 1024 * 1024;

    /// <summary>Read scans at most this many files (newest first), so /charter audit stays bounded.</summary>
    public const int ReadFileLimit = 4;

    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string HashField = ",\"hash\":\"";
    private const int HashSuffixLength = 75;   // ,"hash":"  (9 chars) + 64 hex digits + "}  (2 chars)

    private readonly object _gate = new();
    private readonly string _dir;
    private int _fileIndex;          // the file appended to; 0 = none yet
    private long _nextSeq = 1;
    private string _prevHash = Genesis;

    /// <param name="dataFolder">The plugin data folder (plugins/charter/data); the log lives in its audit/ subfolder.</param>
    public AuditLog(string dataFolder)
    {
        _dir = Path.Combine(dataFolder, "audit");
        try
        {
            Directory.CreateDirectory(_dir);
            Resume();
        }
        catch (Exception e)
        {
            Log.Error($"audit: cannot open {_dir}: {e.Message}");
        }
    }

    /// <summary>Appends one record. Never throws: a failed write is logged and the chain stays at the last record that was written.</summary>
    public void Append(AuditEntry entry)
    {
        try
        {
            lock (_gate)
            {
                byte[] canonical = Canonical(_nextSeq, entry, _prevHash);
                string hash = Convert.ToHexStringLower(SHA256.HashData(canonical));
                string line = Encoding.ASCII.GetString(canonical, 0, canonical.Length - 1) + HashField + hash + "\"}";
                byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");

                if (_fileIndex == 0) _fileIndex = 1;
                string path = PathFor(_fileIndex);
                long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size > 0 && size + bytes.Length > MaxFileBytes)
                {
                    _fileIndex++;
                    path = PathFor(_fileIndex);
                }
                using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }

                _prevHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(line)));
                _nextSeq++;
            }
        }
        catch (Exception e)
        {
            Log.Error($"audit: write failed for {entry.Action}: {e.Message}");
        }
    }

    /// <summary>Checks the whole log: every hash, every prev link, the sequence numbers and the file numbering.
    /// Returns an empty list when intact; otherwise one message naming the first broken record (seq, file, line).</summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>();
        lock (_gate)
        {
            try
            {
                var files = ListFiles();
                string prev = Genesis;
                long expectSeq = 1;
                int expectIndex = files.Count > 0 ? files[0].Index : 1;
                foreach (var (index, path) in files)
                {
                    if (index != expectIndex)
                    {
                        problems.Add($"missing file audit-{expectIndex:0000}.jsonl (the next file is audit-{index:0000}.jsonl)");
                        return problems;
                    }
                    expectIndex++;
                    string name = Path.GetFileName(path);
                    string[] lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        string where = $"{name} line {i + 1}";
                        if (!TryReadChainFields(line, out long seq, out string storedPrev))
                        {
                            problems.Add($"broken at seq {expectSeq} ({where}): record is not valid JSON");
                            return problems;
                        }
                        if (!TryGetStoredHash(line, out string storedHash))
                        {
                            problems.Add($"broken at seq {seq} ({where}): hash field missing");
                            return problems;
                        }
                        string canonicalHash = Convert.ToHexStringLower(SHA256.HashData(CanonicalOf(line)));
                        if (canonicalHash != storedHash)
                        {
                            problems.Add($"broken at seq {seq} ({where}): hash does not match the record (record was changed)");
                            return problems;
                        }
                        if (seq != expectSeq)
                        {
                            problems.Add($"broken at seq {seq} ({where}): expected seq {expectSeq} (record removed, reordered or duplicated)");
                            return problems;
                        }
                        if (storedPrev != prev)
                        {
                            problems.Add($"broken at seq {seq} ({where}): prev does not link to the previous record");
                            return problems;
                        }
                        prev = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(line)));
                        expectSeq++;
                    }
                }
            }
            catch (Exception e)
            {
                problems.Add($"audit log could not be read: {e.Message}");
            }
        }
        return problems;
    }

    /// <summary>Records newest first, at most <paramref name="limit"/>, from the last <see cref="ReadFileLimit"/> files.
    /// <paramref name="subject"/> (when given) matches a record whose subject is that name or uuid (or ends with ":" + it),
    /// or whose detail has it as a word: Core passes the subject inside the detail text, see docs/commands.md.</summary>
    public IEnumerable<AuditRecordView> Read(string? subject, int limit)
    {
        var result = new List<AuditRecordView>();
        if (limit <= 0) return result;
        lock (_gate)
        {
            try
            {
                var files = ListFiles();
                int scanned = 0;
                for (int f = files.Count - 1; f >= 0 && scanned < ReadFileLimit && result.Count < limit; f--, scanned++)
                {
                    string[] lines = File.ReadAllLines(files[f].Path);
                    for (int i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
                    {
                        if (TryView(lines[i], out AuditRecordView view) && Matches(view, subject)) result.Add(view);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"audit: read failed: {e.Message}");
            }
        }
        return result;
    }

    // Continues the chain after a restart: the last physical line gives prev, the last parsable record gives the next seq.
    private void Resume()
    {
        var files = ListFiles();
        if (files.Count == 0) return;
        _fileIndex = files[^1].Index;
        for (int f = files.Count - 1; f >= 0; f--)
        {
            string[] lines = File.ReadAllLines(files[f].Path);
            if (lines.Length == 0) continue;
            _prevHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(lines[^1])));
            long? seq = null;
            int after = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (TryReadChainFields(lines[i], out long s, out _)) { seq = s; break; }
                after++;
            }
            if (seq is { } last)
            {
                _nextSeq = last + after + 1;
            }
            else
            {
                Log.Error("audit: no readable record in the last audit file; the chain continues at seq 1 and verify will report it");
            }
            if (after > 0) Log.Warn($"audit: {after} unreadable line(s) at the end of {Path.GetFileName(files[f].Path)}");
            return;
        }
    }

    private List<(int Index, string Path)> ListFiles()
    {
        var files = new List<(int Index, string Path)>();
        if (!Directory.Exists(_dir)) return files;
        foreach (string path in Directory.EnumerateFiles(_dir, "audit-*.jsonl"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(stem.AsSpan("audit-".Length), out int index) && index > 0) files.Add((index, path));
        }
        files.Sort((a, b) => a.Index.CompareTo(b.Index));
        return files;
    }

    private string PathFor(int index) => Path.Combine(_dir, $"audit-{index:0000}.jsonl");

    private static byte[] Canonical(long seq, AuditEntry e, string prev)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteNumber("time", e.Time);
            w.WriteStartObject("actor");
            w.WriteString("kind", e.Actor.Kind);
            w.WriteString("name", e.Actor.Name);
            if (e.Actor.Id is { } id) w.WriteString("uuid", id.ToString());
            else w.WriteNull("uuid");
            w.WriteEndObject();
            w.WriteString("action", e.Action);
            WriteNullable(w, "subject", e.Subject);
            WriteNullable(w, "detail", e.Detail);
            w.WriteString("prev", prev);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    // The canonical text of a stored line: everything before the hash field, closed with a brace.
    private static byte[] CanonicalOf(string line)
    {
        string text = line[..(line.Length - HashSuffixLength)] + "}";
        return Encoding.ASCII.GetBytes(text);
    }

    private static bool TryGetStoredHash(string line, out string hash)
    {
        hash = "";
        if (line.Length < HashSuffixLength || !line.EndsWith("\"}", StringComparison.Ordinal)) return false;
        int start = line.Length - HashSuffixLength;
        if (string.CompareOrdinal(line, start, HashField, 0, HashField.Length) != 0) return false;
        hash = line.Substring(start + HashField.Length, 64);
        return true;
    }

    private static bool TryReadChainFields(string line, out long seq, out string prev)
    {
        seq = 0;
        prev = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            seq = root.GetProperty("seq").GetInt64();
            prev = root.GetProperty("prev").GetString() ?? "";
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool TryView(string line, out AuditRecordView view)
    {
        view = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var actor = root.GetProperty("actor");
            string kind = actor.GetProperty("kind").GetString() ?? "";
            string name = actor.GetProperty("name").GetString() ?? "";
            view = new AuditRecordView(
                root.GetProperty("seq").GetInt64(),
                root.GetProperty("time").GetInt64(),
                kind == "console" ? "console" : $"{kind}:{name}",
                root.GetProperty("action").GetString() ?? "",
                root.GetProperty("subject").GetString(),
                root.GetProperty("detail").GetString());
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool Matches(AuditRecordView view, string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return true;
        if (view.Subject is { } s && (string.Equals(s, subject, StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(":" + subject, StringComparison.OrdinalIgnoreCase))) return true;
        if (view.Detail is null) return false;
        foreach (string word in view.Detail.Split(' ', '(', ')', ',', ';', '=', ':'))
        {
            if (string.Equals(word, subject, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
