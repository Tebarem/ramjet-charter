using Mc.Scripting;
using Mc.Scripting.Commands;
using Charter.Audit;

namespace Charter.Commands;

// /charter audit [subject] [limit] and /charter audit verify (charter.admin.audit).
internal sealed partial class CharterTree
{
    private const int DefaultAuditLimit = 20;
    private const int MaxAuditLimit = 200;

    private CommandNode AuditRoot()
    {
        // The literal "verify" is added before the subject argument, so it wins over a subject named "verify".
        return Command.Literal("audit").Permission(AuditNode)
            .Executes(ctx => ShowAudit(ctx, null, DefaultAuditLimit))
            .Then(Command.Literal("verify").Executes(ctx => VerifyAudit(ctx)))
            .Then(Command.Argument("subject", Arg.Word()).Suggests(_ => PlayerAndStoreNames())
                .Executes(ctx => ShowAudit(ctx, ctx.GetString("subject"), DefaultAuditLimit))
                .Then(Command.Argument("limit", Arg.Int(1, MaxAuditLimit))
                    .Executes(ctx => ShowAudit(ctx, ctx.GetString("subject"), ctx.GetInt("limit")))));
    }

    private int ShowAudit(CommandContext ctx, string? subject, int limit)
    {
        List<AuditRecordView> rows = _audit.Read(subject, limit).ToList();
        if (rows.Count == 0)
        {
            ctx.Reply(subject is null ? "No audit records." : $"No audit records for {subject}.");
            return 0;
        }
        foreach (AuditRecordView row in rows)
        {
            string detail = row.Detail is null ? "" : $" {row.Detail}";
            ctx.Reply($"#{row.Seq} {Tokens.FormatTime(row.Time)} {row.Actor} {row.Action}{detail}");
        }
        return rows.Count;
    }

    private int VerifyAudit(CommandContext ctx)
    {
        IReadOnlyList<string> problems = _audit.Verify();
        if (problems.Count == 0)
        {
            ctx.Reply("Audit log intact: every hash and link checks out.");
            return 1;
        }
        foreach (string problem in problems) ctx.Reply(problem);
        throw new CommandFailure("The audit log is not intact.");
    }
}
