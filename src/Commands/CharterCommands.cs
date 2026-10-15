using Mc.Scripting;
using Mc.Scripting.Permissions;
using Charter.Audit;
using Charter.Core;

namespace Charter.Commands;

/// <summary>Entry point: registers the admin permission nodes and the /charter tree (alias /perm).</summary>
public static class CharterCommands
{
    /// <summary>Registers the nodes and the commands. Call from OnEnable on the tick thread.</summary>
    /// <param name="plugin">The Charter plugin (owns the commands and nodes).</param>
    /// <param name="core">The Core seam.</param>
    /// <param name="audit">The audit log read by /charter audit.</param>
    public static void Register(Plugin plugin, ICharterCore core, AuditLog audit)
    {
        plugin.Permissions.Register(CharterTree.ViewNode, PermissionDefault.OpLevel(3), "Charter: read permissions, status and dry runs");
        plugin.Permissions.Register(CharterTree.EditNode, PermissionDefault.OpLevel(4), "Charter: change permissions and reload the store");
        plugin.Permissions.Register(CharterTree.AuditNode, PermissionDefault.OpLevel(4), "Charter: read and verify the audit log");
        plugin.Permissions.Register(CharterTree.ImportNode, PermissionDefault.OpLevel(4), "Charter: import and export permission files");

        var tree = new CharterTree(core, audit);
        plugin.Commands.Register(tree.Root("charter"), description: "Manage permissions (Charter)");
        try
        {
            plugin.Commands.Register(tree.Root("perm"), description: "Manage permissions (Charter), same as /charter");
        }
        catch (InvalidOperationException e)
        {
            plugin.Log.Warn($"/perm is taken by another command; use /charter ({e.Message})");
        }
    }
}
