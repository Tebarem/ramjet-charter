# Charter: commands and audit

Files: `src/Commands/*.cs` and `src/Audit/*.cs`. They use Core through `Core/ICharterCore.cs` (see `DESIGN.md`).

## Command reference

Root `/charter`, alias `/perm` (the same tree registered under both names). Permission gates:

| Node | Default | Covers |
|---|---|---|
| `charter.admin.view` | op 3 | the root (every subcommand is hidden without it), reads, `status`, `preview` |
| `charter.admin.edit` | op 4 | every write (`set`, `unset`, `meta`, `prefix`, `suffix`, group and track changes), `reload` |
| `charter.admin.audit` | op 4 | `audit`, `audit verify` |
| `charter.admin.import` | op 4 | `import perms|luckperms`, `export` |

Nodes with a wildcard (`homes.*`, `*`) are typed as a greedy string, because `*` is not a word.

```
user <name|uuid> info
user <name|uuid> check <node> [ctx...]
user <name|uuid> set <node> [true|false] [ctx...] [for <duration>]
user <name|uuid> unset <node> [ctx...]
user <name|uuid> group add <group> [for <duration>]
user <name|uuid> group remove <group>
user <name|uuid> meta set <key> [value]   |   meta unset <key>
user <name|uuid> prefix|suffix add <priority> <text>   |   remove <priority>

group list
group <name> create [weight]    |   delete    |   weight <n>    |   info
group <name> parent add|remove <group>
group <name> set <node> [true|false] [ctx...] [for <duration>]
group <name> unset <node> [ctx...]
group <name> meta set <key> [value]   |   meta unset <key>
group <name> prefix|suffix add <priority> <text>   |   remove <priority>

track list
track <name> create    |   delete    |   append <group>    |   list
track <name> promote <user>    |   demote <user>

preview <any write above>          (dry run: prints the change and the gains and losses)

audit [subject] [limit]            (default 20 records, newest first, limit 1-200)
audit verify

import perms <file> [--apply]
import luckperms <file> [--apply]
export <file>
status
reload
```

### Context and duration tokens

- Context: `world=<name> dimension=<overworld|nether|end|other> gamemode=<survival|creative|adventure|spectator> server=<0-255>` and
  `<slotkey>=<value>` for each declared context slot (values must be declared in the store). Each key at most once.
- Duration: `30s`, `30m`, `2h`, `7d`, `1w2d`. Units s, m, h, d, w. Above 100 years is refused. Stored as `Expires = now + duration`.
- `check` starts from the user's live context (`Core.ContextOf`) and overrides the positions given on the line. A user who is offline
  gets the unknown context, so only "any" patterns match there.

### Track semantics

Track groups are listed lowest first (the order of `track <name> append`).

- `promote <user>`: a user on no track group joins the lowest group. Otherwise the user's highest track group in force is removed and the
  next group up is added. At the top: error.
- `demote <user>`: the highest track group moves one step down. At the bottom the user leaves the track (the lowest group is removed).
  Not on the track: error.
- Only the membership that was moved changes; a user in several track groups keeps the others.

### Group and user semantics

- `set` replaces the rule with the same node and the same context pattern. `unset` with no matching rule is an error.
- `group <name> parent add` refuses a self-parent and an inheritance cycle (checked before the change is handed to Core).
- `group <name> delete` removes the group from every user's memberships, every parent link and every track. `default` cannot be deleted.
- `user ... group add` on a group that does not exist is an error. A user the store has never seen is created on the first write.
- `meta` and `prefix`/`suffix` commands act on the context-free entry. Prefix and suffix use one text per priority: `add` with an existing
  priority replaces the text.
- `group list` and `track list` are the all-items listings. A group or track literally named `list` cannot be addressed by name (use the
  store file or rename it).

## Dry run (`preview`)

`/charter preview <write>` uses the same grammar as the write. The write is applied to a copy of the store (through the same JSON form the
store is written in), then for every online player (or the single target user) the answer `Core.Has` is compared before and after, for
the nodes the change touches:

- the node named on the line (wildcards are expanded over the registered nodes, capped at 1000);
- for changes without a node (membership, track, group delete, parent), the rules of the affected groups and their ancestors, before and
  after.

Output: one line per change (`+` gained, `-` lost), at most 50 lines, then the totals. Nothing is written. Offline players are not checked
by group-scoped previews (the spec asks for online players); a user-scoped preview checks that user even when offline.

## Import and export

- Files are relative to `<data>/import/` (read) and `<data>/export/` (write). Absolute paths, drive letters, `:` and `..` are refused, and
  the resolved path must stay inside the folder.
- `import ... ` without `--apply` prints the report (counts, skipped, remapped and notes, each at most 20 items) and the per-user diff for
  every user in either document: nodes whose `Has` changes, the nodes mentioned by the user's rules and by the rules of its groups (and
  `default`) in either document, at most 50 lines.
- `--apply` runs the importer again on the live copy inside `Core.Change` and replaces the contents with its result, so a store edited
  since the dry run is not overwritten with stale data.
- `export` writes the store JSON through the source-generated `StoreJson` context (write to `.tmp`, then move).

## Audit format

File: `<data>/audit/audit-NNNN.jsonl`, one JSON object per line, rotated when the next line would take a file past 4 MiB, never deleted.
Written by `AuditLog.Append` for every change Core saves.

```
{"seq":12,"time":1760000000,"actor":{"kind":"player","name":"Steve","uuid":"..."},"action":"user.set",
 "subject":null,"detail":"Steve (...) minecraft.command.gamemode = true","prev":"<hex>","hash":"<hex>"}
```

- `seq`: 1 for the first record, +1 for every record, no gaps.
- `prev`: lower-case SHA-256 of the previous line's exact bytes; `"0"*64` for the first record.
- `hash`: lower-case SHA-256 of the canonical text: the same line without the `,"hash":"..."` field, closed with `}`.
- Non-ASCII is escaped by the writer, so each line is pure ASCII and its length is its byte length.
- `verify` checks every file in order (missing file numbers, hash of every record, seq, prev) and reports the first broken record with its
  seq, file and line. It does not stop at the file budget.
- `audit [subject]` reads at most the 4 newest files. A subject matches when the record's `subject` is that name or uuid (or ends with
  `:` plus it), or when the subject is one word of the `detail` text. The command puts the subject name first in `detail`, so the filter
  works without Core passing a subject (see the gaps below).
- A failed write is logged with `Log.Error` and never thrown into the caller. The chain stays at the last record written.

## Decisions

- One tree, two modes (Apply and Preview) built from the same functions, so the dry run cannot drift from the real grammar.
- Writes are parsed into a `ChangePlan` (action, detail, summary, edit on a copy, touched user, groups and node). Core runs the edit; the
  preview runs the same edit on its own copy.
- Node names are typed as greedy strings (wildcards); context and duration are parsed from the rest of the line, so their own tab
  completion is not offered (node, group, track and user names are).
- Typed nodes keep their case; group, track and slot-value names are lower-cased on input.
- Regex is not used anywhere (D12). Parsing is hand-written.
- JSON for the audit is written and read with `Utf8JsonWriter` and `JsonDocument` (no reflection, AOT-safe). The store uses the
  `StoreJson` context.

## Gaps and TODOs

- `ICharterCore.Change` takes no subject, so `AuditEntry.Subject` is null for every command record. Audit filtering works on the detail
  text meanwhile. Planned: Core sets `Subject` from a parameter (or from the uuid in the detail).
- Rate limiting (20 changes per second per actor, spec section 11) is not implemented here; it belongs in Core's `Change`.
- The two-person rule for `*` and sensitive nodes (D5) is not implemented.
- `audit <subject>` cannot filter by a subject named `verify`.
- `group <name>` and `track <name>` cannot target a group or track named `list`.
- Dry runs for offline players are not computed for group-scoped changes (online players only, as specified).
- The store's `Changed` event is not used by these commands.
- Whether `check` should use the live context for online users or the unknown context is a decision (see above); a flag could select it later.
- `Verify` reads every file in full; a very large audit history would make it slow. It runs on the tick thread through the command, so it
  is not suitable for a per-tick check.
