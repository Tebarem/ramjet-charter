using System.Text.Json;
using Mc.Scripting;
using Mc.Scripting.Commands;
using Charter.Import;
using Charter.Core;
using Charter.Model;

namespace Charter.Commands;

// /charter import perms|luckperms <file> [--apply] and /charter export <file> (charter.admin.import).
// Files live only under <data>/import/ (read) and <data>/export/ (write); absolute paths and ".." are refused.
internal sealed partial class CharterTree
{
    private CommandNode ImportRoot()
    {
        return Command.Literal("import").Permission(ImportNode)
            .Then(Command.Literal("perms").Then(ImportFile("perms", (path, doc) => PermissionsJsonImport.Run(path, doc))))
            .Then(Command.Literal("luckperms").Then(ImportFile("luckperms", (path, doc) => LuckPermsImport.Run(path, doc))));
    }

    private CommandNode ImportFile(string kind, Func<string, StoreDoc, ImportReport> run)
    {
        CommandNode file = Command.Argument("file", Arg.String());
        file.Executes(ctx => ImportCommand(ctx, kind, run, apply: false));
        file.Then(Command.Literal("--apply").Executes(ctx => ImportCommand(ctx, kind, run, apply: true)));
        return file;
    }

    private CommandNode ExportRoot()
    {
        return Command.Literal("export").Permission(ImportNode)
            .Then(Command.Argument("file", Arg.String()).Executes(ctx => Export(ctx)));
    }

    private string ResolveFile(string folder, string relative)
    {
        string baseDir = Path.Combine(_core.DataFolder, folder);
        if (!Tokens.TryResolveInside(baseDir, relative, out string full, out string? error))
        {
            throw new CommandFailure(error ?? "invalid file name");
        }
        return full;
    }

    private int ImportCommand(CommandContext ctx, string kind, Func<string, StoreDoc, ImportReport> run, bool apply)
    {
        string relative = ctx.GetString("file");
        string full = ResolveFile("import", relative);
        if (!File.Exists(full)) throw new CommandFailure($"no file import/{relative}");

        ImportReport report;
        try
        {
            report = run(full, _core.Current);
        }
        catch (Exception e) when (e is not CommandFailure)
        {
            throw new CommandFailure($"could not read import/{relative}: {e.Message}");
        }

        ctx.Reply($"Import {kind} from import/{relative}: {report.Result.Groups.Count} group(s), {report.Result.Users.Count} user(s), {report.Result.Tracks.Count} track(s)");
        ReplyList(ctx, "Skipped", report.Skipped);
        ReplyList(ctx, "Remapped", report.Remapped);
        ReplyList(ctx, "Notes", report.Notes);
        ctx.Reply("Changes if applied:");
        ChangePreview.Documents(_core, _core.Current, report.Result, line => ctx.Reply(line));

        if (!apply)
        {
            ctx.Reply("Dry run: nothing written. Run the same command with --apply to commit.");
            return 1;
        }

        // The import runs again on the live copy inside Change, so a store edited since the dry run is not overwritten by stale data.
        ImportReport? applied = null;
        ChangeResult result = _core.Change(ActorOf(ctx), "import", $"{kind} import/{relative}", doc =>
        {
            try
            {
                applied = run(full, doc);
            }
            catch (Exception e) when (e is not CommandFailure)
            {
                return $"could not read import/{relative}: {e.Message}";
            }
            DocEdits.ReplaceContents(doc, applied.Result);
            return null;
        });
        if (!result.Ok) throw new CommandFailure(result.Error ?? "the import was refused");
        ctx.Reply($"Imported {kind} from import/{relative} ({result.SubjectsChanged} changed)");
        return 1;
    }

    private int Export(CommandContext ctx)
    {
        string relative = ctx.GetString("file");
        string full = ResolveFile("export", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string json = JsonSerializer.Serialize(_core.Current, StoreJson.Default.StoreDoc);
        string temp = full + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, full, overwrite: true);
        ctx.Reply($"Exported the store to export/{relative} ({json.Length} characters)");
        return 1;
    }
}
