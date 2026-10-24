using System.Windows;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Host actions shared by the Hosts page and the Session Manager pane (0.6.0): add a host, edit one, add a folder. Moved
/// unchanged out of HostsView so both places do exactly the same thing — restrictions, locks, shared-library hosts and
/// the audit log included.
/// </summary>
internal static class HostActions
{
    /// <summary>The add-host dialog, seeded with <paramref name="folder"/> and <paramref name="protocol"/>. Returns the
    /// new host, saved, or null.</summary>
    public static HostEntry? Add(Window? owner, string? folder, string protocol = "ssh")
    {
        if (!Restrictions.Check("host.add")) return null;
        var seed = folder != null || protocol != "ssh"
            ? new HostEntry
            {
                Group = folder ?? "General", Protocol = protocol,
                Username = protocol switch { "rdp" => "Administrator", "vnc" => "", _ => AppSettings.Current.DefaultUsername },
                Port = protocol switch { "rdp" => 3389, "vnc" => Vnc.VncConnector.DefaultPort, _ => 22 },
                Auth = protocol == "ssh" ? AuthMethod.PrivateKey : AuthMethod.Password,
            }
            : null;
        var w = new HostEditWindow(seed, seedIsNew: true) { Owner = owner };
        if (w.ShowDialog() != true || w.Result is not { } h) return null;
        AppData.Current.Hosts.Add(h);
        AppData.Save();
        AuditLog.Write("you", h.Display, "added host");
        return h;
    }

    /// <summary>The host editor for <paramref name="h"/> (a locked host asks to unlock first; a shared one opens the
    /// library's editor).</summary>
    public static void Edit(Window? owner, HostEntry h)
    {
        if (h.IsShared) { SharedLibraryUi.EditHost(h, owner); return; }
        if (!Restrictions.Check("host.edit")) return;
        if (!h.IsAdHoc && !Locks.IsOpen("host:" + h.Id))
        {
            _ = Application.Current.Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("host:" + h.Id, "edit " + h.Display)) Edit(owner, h); });
            return;
        }
        var w = new HostEditWindow(h) { Owner = owner };
        if (w.ShowDialog() != true || w.Result is not { } edited) return;
        var i = AppData.Current.Hosts.FindIndex(x => x.Id == h.Id);
        if (i >= 0) AppData.Current.Hosts[i] = edited;
        Ssh.SshPool.Drop(h.Id);
        AppData.Save();
        AuditLog.Write("you", edited.Display, "edited host");
        if (Envs.Normalize(h.Environment) != Envs.Normalize(edited.Environment))
            AuditLog.Write("you", edited.Display, "environment", $"{(h.Environment.Length > 0 ? h.Environment : "(not set)")} → {(edited.Environment.Length > 0 ? edited.Environment : "(not set)")}");
        Envs.RaiseChanged();
    }

    /// <summary>Asks for a folder name inside <paramref name="parent"/> ("" = top level) and adds it. <paramref name="beforeSave"/>
    /// runs with the new path before the data is saved (a view selects it before it redraws). Returns the path or null.</summary>
    public static string? NewFolder(string parent, Action<string>? beforeSave = null)
    {
        if (!Restrictions.Check("folder.edit")) return null;
        if (Dialogs.Prompt("New folder", parent.Length > 0 ? $"New folder inside “{parent}” (use / for nested, e.g. EU/Web):" : "Folder name (use / for nested, e.g. Production/EU):") is not { Length: > 0 } name) return null;
        var path = AppData.NormalizeFolder(parent.Length > 0 ? parent + "/" + name : name);
        if (path.Length == 0) return null;
        if (!AppData.Current.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) AppData.Current.Folders.Add(path);
        beforeSave?.Invoke(path);
        AppData.Save();
        return path;
    }
}
