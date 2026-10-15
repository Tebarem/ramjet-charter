# Charter

Ramjet's permissions plugin: groups, inheritance, contexts, temporary grants, tracks, meta and prefixes/suffixes (`IPlayerMeta`),
a hash-chained audit log, and imports from Ramjet's `permissions.json` and LuckPerms export files. Clean-room: written from its own design
(`src/DESIGN.md`) and public documentation only; no code from other permissions plugins.

- `src/`: the plugin, a Ramjet source plugin. Deploy it as `plugins/charter/` (the server compiles it; `src/charter.csproj` only exists
  for an out-of-server compile check). Design and ownership notes: `src/DESIGN.md`, `src/docs/`.
- `tests/Charter.Tests/`: resolver and import tests (console app).

Build and test against an engine build: set `RAMJET_HOME` to the engine's `build/bin` (the folder with `dotnet/Mc.Scripting.Api.dll`).

```bash
dotnet build src/charter.csproj
dotnet run --project tests/Charter.Tests
```

## License

MIT, see `LICENSE`.
