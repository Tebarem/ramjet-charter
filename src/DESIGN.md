# Charter: design

Charter is a permissions plugin for the Ramjet server. Its one structural idea: **the engine answers every permission check, and the plugin
decides what the answers are.** Charter owns the semantics (groups, inheritance, contexts, expiry, tracks, meta) and compiles them into compact
rules that it pushes into the engine's native permission table. Command trees, the command dispatcher and other hot paths then check bits and
short rule lists inside the engine, with no round trip to the plugin.

Labels such as D3 or D12 refer to the decisions of the original design spec; the parts that matter are restated here and in `docs/`.

**Clean room**: no code was read or copied from LuckPerms or any other permissions plugin. Only public behaviour and data formats were used
(command shapes, node conventions, the export file format for importing).

## Packaging

`src/` is the source plugin folder: deploy it as `plugins/charter/`. The server compiles every `.cs` below it with Roslyn: no NuGet packages,
one assembly. System.Text.Json source generation works; reflection-based JSON does not (the plugin worker is AOT compiled), so every JSON type
has a `[JsonSerializable]` context. `charter.csproj` only exists to compile the plugin outside the server against `RAMJET_HOME`. The plugin's
data folder is `plugins/charter/data/` (`Plugin.DataFolder`), the only place it writes.

## Scope (v1)

Store, groups, weights, inheritance, wildcards, the default group, the context tuple, temporary rules and memberships, tracks, typed meta and
prefixes/suffixes (`IPlayerMeta`), a hash-chained audit log, `/charter` (alias `/perm`), import from Ramjet's `permissions.json` and from a
LuckPerms export file, dry-run diffs and `/charter status`. Not in v1: role templates, a web editor, network-wide sync.

## Layout

| Area | Files | Notes |
|------|-------|-------|
| Model | `Model/Store.cs` | The store document (`data/store.json`) and its JSON context. |
| Core | `Core/*.cs`, `CharterPlugin.cs` | Store file, pure resolver, compiler to native rules, table sync, permission provider, `IPlayerMeta`, expiry. `Core/ICharterCore.cs` is the seam the other areas use. See `docs/core.md`. |
| Commands, audit, tracks | `Commands/*.cs`, `Audit/*.cs` | `/charter` and `/perm`, preview, the audit log. See `docs/commands.md`. |
| Import | `Import/*.cs` | `permissions.json` and LuckPerms export importers. See `docs/import.md`. |
| Tests | `../tests/Charter.Tests/` | Resolver and import tests (console app, outside the plugin folder). |

Interfaces between areas:
- `ICharterCore`, implemented in `Core/CharterCore.cs`.
- `IAuditSink`, implemented by `Audit/AuditLog.cs` (`AuditLog(string dataFolder)`, `Verify()`, `Read(subject, limit)`).
- `Commands/CharterCommands.Register(plugin, core, audit)` registers the command tree.
- Importers: `Import/PermissionsJsonImport.Run(path, current)` and `Import/LuckPermsImport.Run(path, current)` return an `ImportReport` (the new
  document plus skipped, remapped and note lists). The commands compute the dry-run diff with `ICharterCore.Resolve(..., doc)`.

## Resolution and layers

User rules are layer 0. Then come the user's effective groups in order: own groups sorted by weight (highest first, ties by name), each followed
by its parents (depth first, parents sorted by weight, a group appears once at its first position, cycles tolerated), then `default` and its
parents last. Layer n is position n in that list (capped at 1023).

Inside a layer: the matching rule with the most specific context wins, then the most specific node (exact > longest wildcard > `*`), then a
denial over a grant. Expired entries never match. The compiler emits exactly this as `NativeRule(node, allow, layer, context bytes)` for the
engine's table, so the engine's answer equals the pure resolver's (the tests check the resolver against this order).

Context bytes (6): world index, dimension, gamemode, server, slot 0, slot 1; `0xFF` means any. A rule naming a world the server does not know
can never match an online check, so Core maps world names to the indexes it learns from the server and logs the names it cannot map.

Operators of level 4 are never locked out: the engine always allows them (break-glass), whatever the rules say.

## Code conventions

Nullable on, no warnings (warnings are errors in `charter.csproj`), `namespace Charter.<Area>;`, no reflection, no `dynamic`, and no regex on
check paths (regex nodes are not supported, by design).
