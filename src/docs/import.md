# Charter import

Files: `src/Import/*.cs`. Tests: `tests/Charter.Tests/` (see below).

## Public API

Both entry points return an `ImportReport` and never change the document they are given.

```csharp
public static ImportReport Run(string path, StoreDoc current);   // PermissionsJsonImport, LuckPermsImport
```

`ImportReport`: `Result` (the merged `StoreDoc`, a deep copy of `current`), `Skipped`, `Remapped`, `Notes` (lists, each capped at
5000 lines; `SkippedCount`, `RemappedCount`, `NotesCount` count everything). `ImportLimits` holds the caps.

Failures are exceptions, not report entries: `InvalidDataException` for a file that is too large, not JSON, or not the right top-level
shape. The commands area catches it and shows the message. `FileNotFoundException` / `IOException` come straight from the file system.

Merge rule (both importers): a group or user in the file replaces a same-named one in the store, and the replacement is noted. Groups
and users that are not in the file are kept. `default` is always present in the result.

## Ramjet `permissions.json` (`PermissionsJsonImport`)

Format: the built-in backend's file (`FilePermissionProvider.cs`). Parsed with the engine's rules: comments and trailing commas are
allowed. Limit: 16 MiB.

| Ramjet | Charter |
|---|---|
| `groups.<name>.weight` | `GroupDoc.Weight` |
| `groups.<name>.inherits` | `GroupDoc.Parents` (`default` dropped: implicit, the engine ignores it too) |
| `groups.<name>.prefix` / `suffix` (non-empty) | `Prefixes` / `Suffixes`, `Priority` = the group's weight |
| `groups.<name>.permissions` | `Rules`; `"-node"` = `node` with `Value = false` (later entry for the same node wins, as the engine does) |
| `users.<uuid>.name` | `UserDoc.Name` (empty = null) |
| `users.<uuid>.groups` | `UserDoc.Groups` (`default` dropped) |
| `users.<uuid>.permissions` | `UserDoc.Rules` |
| top-level `version` (must be 1 to be read as is) | noted if different |
| any other key | kept under `Extra` on its object, noted |

Remapped: group names lower-cased (also in `inherits` and user `groups`), user keys normalised to lower-case dashed uuids.
Skipped: a group or user key that collides with an earlier one after normalisation (the earlier one is kept), a non-uuid user key, a
non-object group or user, a non-boolean permission value, a permission beyond 4096 per group or user, a weight that is not an integer.
Notes also say which groups replace the store's, and which memberships point at groups that are not in the store.

## LuckPerms export (`LuckPermsImport`)

Format: the file LuckPerms' export command writes, read from a gzip file (detected by its magic bytes, not its name) or plain JSON.
Top level: `groups` (name -> `{ nodes: [...] }`), `users` (uuid -> `{ username, primaryGroup, nodes: [...] }`), `tracks`
(name -> `{ groups: [...] }`). Other top-level keys are ignored and noted. A node is `{ key, value, expiry?, context? }`. Limit: 64 MiB
of decompressed JSON, checked while decompressing (`LimitedStream`).

Node keys:

| Key | Charter |
|---|---|
| `group.<name>` (value true) | membership: `GroupDoc.Parents` for a group, `UserDoc.Groups` for a user; with its context and expiry |
| `weight.<n>` (group only, permanent, context-free) | `GroupDoc.Weight` |
| `prefix.<priority>.<text>` / `suffix.<priority>.<text>` | `Prefixes` / `Suffixes` with that priority |
| `meta.<key>.<value>` | `MetaDoc` (escapes removed from key and value) |
| `displayname.<name>` (group only, permanent, context-free) | `GroupDoc.DisplayName` |
| `r=...` or `R=...` | skipped and reported (D12: no regex in v1) |
| anything else | a rule: `RuleDoc` with the node and `value`; a leading `-` strips the dash and sets `Value = false` |

Escapes: a backslash makes the next character literal. Dots inside meta and affix text are escaped in the export and are unescaped on
read. The field split happens on unescaped dots only.

Contexts (the store's fixed tuple, D2):

| Context key | Charter | Notes |
|---|---|---|
| `world` | `ContextDoc.World` | the name as written; empty name skipped |
| `gamemode` | `ContextDoc.Gamemode` | one of survival, creative, adventure, spectator (lower-cased); other values skipped |
| `server` | `ContextDoc.Server` | a number 0-255; `global` means any server; any other value skips the entry |
| `dimension`, `dimension-type` | `ContextDoc.Dimension` | `overworld`, `the_nether` (`nether`), `the_end` (`end`), `other`, optional `minecraft:` prefix; other values skip the entry; a mapped name is remapped |
| any other key | skipped | reported |
| `global` (any key) | no constraint | the key is treated as absent |

A key with several values yields one rule per value; several keys multiply out (capped at 64 combinations). An entry whose context
cannot be represented is skipped with its reason. It is never imported without the context, because that would widen it.

Expiry: `expiry` is unix seconds. Entries with `expiry < now` are skipped and counted. A future expiry is kept on the membership, the
rule, the affix, or the meta entry.

Users: a user with nothing beyond default membership is skipped and counted (summary note). A `primaryGroup` other than default that has
no membership of its own becomes a permanent membership, with a note. A user key that is not a uuid is skipped.

Tracks: the groups must exist in the result (the store's groups count). Missing groups are dropped from the track and reported.

Skipped, always reported with the node key: negated memberships, contextual or temporary weight or display name, display names on users,
weights on users, malformed prefix/suffix/meta keys, regex nodes, expired nodes, unsupported contexts, a node beyond 4096 rules per
group or user.

## Mapping table (summary)

| LuckPerms | Charter | Status |
|---|---|---|
| group, weight | group, weight | done |
| user, group membership, temporary and context-bound membership | `Membership` | done |
| node true/false, context, expiry | `RuleDoc` (context per D2) | done |
| wildcard node | `RuleDoc.Node` ending in `.*` or `*` | done (kept as written) |
| prefix / suffix / meta | affixes / `MetaDoc` | done |
| displayname | `GroupDoc.DisplayName` | done (permanent, context-free only) |
| track | `TrackDoc` | done (existing groups only) |
| regex node | skipped (D12) | done, reported |
| plugin context key | skipped | reported; the admin can map it to a slot later |
| primary group | membership | done, noted, to verify |

## Assumptions to verify against a real LuckPerms export

These come from working knowledge of the export format. Fix them once a real export is captured from a test server (the spec's test
plan asks for that):

1. Context values: a single string or a list of strings are both accepted; numbers are accepted and read as text.
2. `server: "global"` and `world: "global"` mean any. That is the intended meaning of LuckPerms' global context, but check it.
3. `expiry` is in seconds. A millisecond export would make every entry look expired and be reported as such.
4. A `-node` key is a denial in Charter (the Ramjet meaning). If LuckPerms uses it for something else, change `ReadRule`.
5. `primaryGroup` becomes a membership when none exists. The alternative is to drop it and note it.
6. `value` missing on a node is read as true.
7. Group names and track names are lower-cased. LuckPerms names are case-insensitive, so this should be the same.

## Gaps

- Plugin context keys (slots) are not imported. A key other than the four above is skipped.
- Temporary or contextual weights and display names are skipped (they have no place in `GroupDoc`).
- A negated membership (`group.x = false`) is skipped. The store has no way to express "not in this group".
- Regex nodes are skipped (D12).
- Duplicate meta keys are all kept (the store's meta is a list). The reader decides what to do with more than one value.
- Import does not check node syntax, group-name rules or counts beyond the import caps. Core's validation runs when the result is saved.
- The caps in `ImportLimits` (4096 rules per subject, 1024 groups, 50 000 users) are import-side bounds; Core's validation applies its own
  limits when the result is saved.

## Tests

`tests/Charter.Tests/` is a console project (`net10.0`, no NuGet). It compiles `src/Model`, `src/Import`,
`src/Core/ICharterCore.cs` and `src/Core/Resolver.cs` directly, and references `Mc.Scripting.Api.dll` from `RAMJET_HOME`.
Run it with `dotnet run --project tests/Charter.Tests`. The optional argument is a name filter. The exit code is the number of failed
checks.

- `ImportTests.cs`: golden checks over `fixtures/permissions.json` and `fixtures/luckperms-export.json`. The LuckPerms export is also
  gzipped at test time. Also: the input document is not modified, a file over the size limit is refused, and malformed input is refused.
- `ResolverTests.cs`: the D3 rules from DESIGN.md, written from the spec: the user layer, weight, parents after children, default last,
  cycles, context and node specificity, deny over grant, expiry, and unknown query positions.
