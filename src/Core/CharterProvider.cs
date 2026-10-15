using System.Collections.Generic;
using System;
using Mc.Scripting.Permissions;
using Mc.Scripting;

namespace Charter.Core;

/// <summary>The permission backend Charter installs (<c>plugin.Permissions.SetProvider</c>, so it is restored when Charter unloads). A node with no rule
/// returns Unset and the node's registered default decides, as for every backend.</summary>
public sealed class CharterProvider : IPermissionProvider
{
    private readonly CharterCore _core;

    public CharterProvider(CharterCore core) => _core = core;

    /// <inheritdoc/>
    public string Name => "charter";

    /// <inheritdoc/>
    public PermissionValue Get(Uuid player, string node) => _core.ExplicitValue(player, node);

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Groups(Uuid player) => _core.EffectiveGroups(player, _core.ContextOf(player));

    /// <inheritdoc/>
    public event Action<Uuid?>? Changed
    {
        add => _core.Changed += value;
        remove => _core.Changed -= value;
    }
}
