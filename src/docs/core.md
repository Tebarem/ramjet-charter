# Charter core

Files: `src/Core/*.cs`, `src/CharterPlugin.cs`, `src/plugin.json`, `src/charter.csproj`. The seam to the other areas is
`Core/ICharterCore.cs`; the store document is `Model/Store.cs` (see `DESIGN.md`).

## What is built

| File | What |
|---|---|
| `Core/StoreFile.cs` | Load (missing file = default group; syntax errors carry the line; newer schema refused), `Normalize`, `Validate` (names, nodes, contexts, counts), atomic `Save` (`store.json.tmp`, flushed, `File.Replace` to `store.json` with `store.json.bak`), `DeepCopy` (StoreJson round trip). |
| `Core/Resolver.cs` | The pure D3 resolver. `Resolve(doc, user, node, ctx, now)`, `EffectiveGroups(doc, user, ctx, now)`, and the internal plan, candidate collection and context patterns that the compiler and the meta service share. |
| `Core/Compiler.cs` | `ContextMap` (world key to index from `Server.Worlds`, dimension, game mode) and `Compiler.Compile(doc, user, now, map, notes)`: the native rules of one subject. |
| `Core/TableSync.cs` | Claim, snapshot, per-subject push (SetSubject, then DeltaSubject chunks above 512 rules), join and quit, refused-message retry. |
| `Core/CharterProvider.cs` | `IPermissionProvider` named "charter". |
| `Core/MetaService.cs` | `IPlayerMeta`: `TryGet<T>` (string, int, bool, TimeSpan), `Affixes`, `Changed`. |
| `Core/Expiry.cs` | D4: one scheduled task for the earliest expiry, fires a change `expire`, reschedules. |
| `Core/CharterCore.cs` | `ICharterCore`: the change pipeline, rate limit, `Has`, `ContextOf`, `TryFindUser`, `Status`, `Reload`. |
| `CharterPlugin.cs` | Enable and disable, node registration, meta service, events, commands and audit wiring. |

## Decisions

**Resolution.** The layer order is computed from the live memberships with contexts ignored, so the native table and the resolver use the same layers. A membership's context does not move a group; it narrows the group's entries. Each group has one or more *context paths* (the intersection of the membership contexts from the user or from `default`); a rule is active under the intersection of its own context and a path. This is how a membership with a context reaches the table without per-query state. Specificity inside a layer counts the fixed positions of that intersection. `default` is always placed last, and it is skipped as a parent everywhere else.

**Node matching.** Exact, then `a.b.*` (any depth below `a.b`), then `*`. Specificity is exact, then the longer prefix, then `*`. Within a layer: more specific context, then more specific node, then a denial.

**Compile.** Rules with unknown world names or slot values are skipped with one log line each (cached so the log does not repeat). Entries with the same (node, layer, context) keep the denial: the resolver would pick it anyway, and the table's delta upserts need unique keys. More than 4096 rules keeps the shallowest layers and logs.

**Table feed.** Only online players are pushed (the engine answers only for players it serves). Offline store users are pushed when they join and dropped when they quit. Each commit compiles every online player and sends only the subjects whose rules differ from what the table holds. A refused message schedules a full snapshot after 1 tick, doubling up to 200 ticks, and the last state keeps answering in the meantime. Slot values are sent as unknown (255) because nothing sets them yet.

**Hot reload and claim ownership.** `OnDisable` has no reload reason (`PluginDisableEvent` is "unload or hot reload", `Plugin.OnDisable()` takes no argument, and `PluginContext.Supersedes` is internal). So each enable writes a fresh token into `AppContext` (process-wide, shared across load contexts). `OnDisable` releases the claim only when the token is still its own, that is on a real unload. A successor that enables first takes the token and the claim, and the predecessor leaves them alone.

**Store not usable.** If `store.json` fails to load at enable, Charter logs it loudly, releases any claim, does not install the provider (the built-in backend keeps answering), and refuses every change until `/charter reload` succeeds. Nothing is written over a broken file.

**Change pipeline.** Edit a deep copy, normalise, validate, save atomically, then diff against the old document (group-level changes mean everyone), sync the table, reschedule expiry, raise `Changed` (per user, or once for everyone above 128 users or on group changes), audit. A failed step leaves everything as it was. Rate limit: 20 changes per second per actor; the system (expiry, reload) is exempt.

**Break-glass.** `Has` and the provider answer True for an online operator of level 4 before any rule. The engine's table must do the same (see integration requests): a deny rule for `*` cannot be overridden by any rule Charter can push.

**Validation limits** (beyond the contract's own): meta 256 entries per subject, affixes 64 per kind, memberships 64 per subject, tracks 256 with 64 groups each, affix text 256 characters, meta value 1024, world name 96. Membership, parent and track references to groups that do not exist are errors.

**Audit.** `AuditEntry.Time` is unix seconds (UTC). Subject is null for store changes; the detail carries the caller's text. Expiry records as actor "system", action "expire".

**Expiry.** Earliest-first, one task, waits at most one hour per step, and waits one minute after a refused save instead of retrying at every tick.

## Public API (what other areas call)

- `CharterCore(string dataFolder, Scheduler, Logger)`; `Load()` (error text or null), `Start()`, `Reload()`, `Change(...)`, `Current`, `LoadError`, `Started`, `OnStarted`, `Audit` (`IAuditSink?`), `Changed`.
- `ICharterCore` as contracted: `Change`, `Reload`, `Resolve`, `Has`, `ContextOf`, `EffectiveGroups`, `TryFindUser`, `Status`, `Changed`, `DataFolder`, `Current`.
- Extras on `CharterCore`: `ExplicitValue(uuid, node)`, `OnPlayerJoined`, `OnPlayerLeft`, `OnContextChanged`, `static NowSeconds()`.
- `StoreFile` (`Load`, `Save`, `Validate`, `Normalize`, `DeepCopy`, `CreateEmpty`, `IsValidName`, `ValidateNode`), `Resolver` (`Resolve`, `EffectiveGroups`), `Compiler.Compile`, `ContextMap` (`FromServer`, `DimensionOf`, `Dimension`, `GameMode`), `CharterProvider`, `MetaService`.

## Engine APIs used (and where they were found)

- `NativePermissionTable`: `Claim`, `Apply`, `SetSubject`, `DeltaSubject`, `DropSubject`, `BeginSnapshot`, `EndSnapshot`, `Stats`, `Any`, `MaxRulesPerMessage`, `MaxMessageBytes` (`Permissions/NativePermissionTable.cs`).
- `PermissionManager`: `IsRegistered`, `Registered`, `Has(Uuid, node)` (`Permissions/PermissionManager.cs`). The last is used only for unregistered vanilla command nodes (see gaps).
- `PluginPermissions.SetProvider`, `Register(node, default, description)` (`Permissions/PluginPermissions.cs`). `Register` already declares the defaults in the table, so Charter does not call `Declare`.
- `IPermissionProvider` (`Permissions/FilePermissionProvider.cs`); `PermissionDefault.OpLevel`, `Evaluate` (`Permissions/Permission.cs`); `PermissionValue` (same file).
- `IPlayerMeta`, `MetaKey<T>`, `Affix`, `AffixKind` (`Services/PlayerMeta.cs`); `ServiceManager.Register<T>` (`Services/ServiceManager.cs`, via `Plugin.Services`).
- `Scheduler.RunLater`, `ScheduledTask.Cancel`, `IsCancelled` (`Scheduler.cs`).
- `Server.Players`, `Server.GetPlayer(Uuid|string)`, `Server.Worlds` (`Server.cs`); `World.Key`, `World.Index` (`Worlds/World.cs`); `Player.Uuid`, `Name`, `World`, `GameMode`, `PermissionLevel` (`Players/Player.cs`, `Players/Player.State.cs`); `GameMode` enum (`Generated/Events.g.cs`).
- `OfflinePlayers.Find`, `OfflinePlayers.Get` (`OfflinePlayers.cs`).
- Events `PlayerJoinEvent`, `PlayerQuitEvent`, `PlayerChangedWorldEvent`, `PlayerGameModeChangeEvent` (`Generated/Events.g.cs`), `Events.On((ref T e) => ...)` (`Events.cs`, shape as in `samples/Greeter`).
- `Plugin.DataFolder`, `Log`, `Scheduler`, `Permissions`, `Services`, `Events` (`Plugin.cs`); `Logger` (`Log.cs`).
- Not used: `PluginConfig.ReadText/WriteText` (they cannot keep a backup generation; the store is written in the data folder with `File.Replace`).
- BCL: `AppContext.SetData/GetData` (process-wide claim owner).

## Gaps and assumptions to check

1. **Break-glass in the table.** The engine must answer True for an op-4 subject before any table rule. Otherwise a `*` deny in the store locks the operator out of table-checked commands.
2. **Offline operator level.** `OpLevel` returns 0 for offline players: the uuid level (`Host.UuidPermissionLevel`) is internal. Affects `Has` for offline users only.
3. **Vanilla command default level.** Unregistered `minecraft.command.*` nodes fall back to `PermissionManager.Has(uuid, node)`, which is correct only when the context is the player's own (the code checks that). Otherwise the answer is "denied".
4. **Slots.** No API sets slot values or declares slots at run time (`RegisterContextSlot` of the spec is not in the API). Slot-bound rules never match.
5. **Hot reload reason.** `OnDisable` cannot tell a reload from an unload; the token in `AppContext` is the workaround.
6. **Offline changes.** Offline users are not pushed to the table; they are pushed on join. Their `Changed(uuid)` is raised anyway (meta listeners).
7. **Meta contexts.** `IPlayerMeta` has no context parameter: `TryGet` and `Affixes` use the player's current context (the API's own note says contexts are not part of this contract yet).
8. **Membership-context ordering.** Layers ignore membership contexts by design; a provider with contexts in very deep chains may see a group's rules counted in a layer position computed without the context. Documented, not measured.
9. **Atomicity.** `File.Replace` is atomic on the same volume; the temporary file is flushed, the directory is not.
10. **Tests.** The pure resolver is covered by `tests/Charter.Tests`; table sync, the provider and expiry are checked on a running server.

## Wanted from the engine API

- Done: op-4 break-glass in the engine's permission table.
- A public uuid operator level (`PermissionManager.OpLevel(Uuid)`) and a public vanilla command default level (`PermissionManager.DefaultLevel(node)`).
- A way to declare context slots and set a player's slot value (`Permissions.RegisterContextSlot`, `Permissions.SetSlot(uuid, slot, value)`), or a provider hook that returns slot values.
- A reason on `Plugin.OnDisable` (reload or unload) or `PluginContext.IsSuccessorEnabled`.
