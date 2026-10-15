using Charter.Core;
using System;
using Mc.Scripting.Events;
using Mc.Scripting.Permissions;
using Mc.Scripting.Services;
using Mc.Scripting;

namespace Charter;

/// <summary>Charter, the permissions plugin. Enable loads the store and, when it is usable, claims the native table and installs the provider. A store
/// that does not load leaves the built-in backend answering and Charter idle until <c>/charter reload</c> succeeds.</summary>
public sealed class CharterPlugin : Plugin
{
    // AppContext is process wide (shared by every load context), so it can name the instance that owns the native claim across a hot reload
    private const string ClaimOwnerKey = "Charter.TableClaimOwner";

    private string _token = "";

    protected override void OnEnable()
    {
        _token = Guid.NewGuid().ToString("N");
        AppContext.SetData(ClaimOwnerKey, _token);

        var core = new CharterCore(DataFolder, Scheduler, Log);
        core.OnStarted = () => Permissions.SetProvider(new CharterProvider(core));
        var audit = new Charter.Audit.AuditLog(DataFolder);
        core.Audit = audit;

        string? loadError = core.Load();
        if (loadError is null)
        {
            core.Start();
        }
        else
        {
            Log.Error("STORE NOT LOADED, Charter is idle: " + loadError);
            Log.Error("the built-in permission backend keeps answering; fix store.json and run /charter reload");
            NativePermissionTable.Claim(false);  // a predecessor's claim must not keep answering with its rules
        }

        Permissions.Register("charter.admin.view", PermissionDefault.OpLevel(3), "View /charter status, lists and the audit summary");
        Permissions.Register("charter.admin.edit", PermissionDefault.OpLevel(4), "Change groups, users, rules, meta and tracks");
        Permissions.Register("charter.admin.audit", PermissionDefault.OpLevel(4), "Read and verify the audit log");
        Permissions.Register("charter.admin.import", PermissionDefault.OpLevel(4), "Import permissions from other files");

        Services.Register<IPlayerMeta>(new MetaService(core));

        Events.On((ref PlayerJoinEvent e) => core.OnPlayerJoined(e.Uuid));
        Events.On((ref PlayerQuitEvent e) => core.OnPlayerLeft(e.Uuid));
        Events.On((ref PlayerChangedWorldEvent e) => core.OnContextChanged(e.Player.Uuid));
        Events.On((ref PlayerGameModeChangeEvent e) => core.OnContextChanged(e.Player.Uuid));

        Charter.Commands.CharterCommands.Register(this, core, audit);
    }

    protected override void OnDisable()
    {
        // A hot reload enables the successor before this instance is disabled. The successor has replaced the owner token, so the claim is theirs and stays.
        // On a real unload our token is still there: the claim is released and the built-in backend answers again. The provider and the registrations
        // are removed by the plugin's own release, after this method.
        if (AppContext.GetData(ClaimOwnerKey) is string owner && owner == _token)
        {
            AppContext.SetData(ClaimOwnerKey, null);
            NativePermissionTable.Claim(false);
        }
    }
}
