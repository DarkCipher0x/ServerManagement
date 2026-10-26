namespace DcxManager.Services;

/// <summary>A host folder's defaults (0.6.29, ADR-105). null = not set here, so the parent folder's applies; "" for the
/// jump host, terminal profile and VPN means "direct", "the default profile" and "none", overriding a parent.</summary>
public sealed class FolderSettings
{
    public string Path { get; set; } = "";
    public string? Username { get; set; }
    public string? CredentialId { get; set; }
    public string? JumpHostId { get; set; }
    public string? Environment { get; set; }
    public string? TerminalProfileId { get; set; }
    public string? VpnProfileId { get; set; }
    /// <summary>Always-on tmux: "" (Settings decides), "on" or "off".</summary>
    public string? Tmux { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => FolderDefaults.Fields.All(f => FolderDefaults.ValueOf(this, f) == null);
}

/// <summary>
/// Folder inheritance (0.6.29, ADR-105; DarkCipher0x: change 50 hosts in one place). A folder can set a user name, credential,
/// jump host, environment, terminal profile, VPN and always-on tmux for the hosts in it and its sub-folders; the nearest
/// folder that sets a field wins. A host follows only the fields listed in its <see cref="HostEntry.Inherits"/>: the
/// folder's value is written onto the host (so every connection path reads the host as before), and written again
/// whenever the folder's default changes, the host moves, or the folder is renamed. Every other field stays the host's own.
/// In the host editor a field follows its folder while it shows the folder's value; changing it makes it the host's own.
/// </summary>
public static class FolderDefaults
{
    public static readonly string[] Fields = { "user", "credential", "jump", "environment", "profile", "vpn", "tmux" };

    public static string Title(string field) => field switch
    {
        "user" => "user name", "credential" => "credential", "jump" => "jump host", "environment" => "environment",
        "profile" => "terminal profile", "vpn" => "VPN", "tmux" => "tmux", _ => field,
    };

    public static List<FolderSettings> All => AppData.Current.FolderDefaults;

    public static FolderSettings? For(string folder)
    {
        var path = AppData.NormalizeFolder(folder);
        return All.FirstOrDefault(f => AppData.NormalizeFolder(f.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Stores a folder's defaults (an empty set removes them), then applies them to the hosts that follow.</summary>
    public static void Set(FolderSettings s)
    {
        s.Path = AppData.NormalizeFolder(s.Path);
        All.RemoveAll(f => AppData.NormalizeFolder(f.Path).Equals(s.Path, StringComparison.OrdinalIgnoreCase));
        if (!s.IsEmpty) All.Add(s);
        Apply(AppData.Current.Hosts);
    }

    public static string? ValueOf(FolderSettings s, string field) => field switch
    {
        "user" => s.Username, "credential" => s.CredentialId, "jump" => s.JumpHostId, "environment" => s.Environment,
        "profile" => s.TerminalProfileId, "vpn" => s.VpnProfileId, "tmux" => s.Tmux, _ => null,
    };

    /// <summary>The nearest folder (the host's own, then each parent) that sets <paramref name="field"/>: its value and path.</summary>
    public static (string Value, string Folder)? Effective(string group, string field)
    {
        if (All.Count == 0) return null;
        for (var f = AppData.NormalizeFolder(group); f.Length > 0; f = f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            if (For(f) is { } s && ValueOf(s, field) is { } v) return (v, f);
        return null;
    }

    /// <summary>The host's value of a field, as the folder stores it (no jump host / profile / VPN = "").</summary>
    public static string HostValue(HostEntry h, string field) => field switch
    {
        "user" => h.Username, "credential" => h.CredentialId ?? "", "jump" => h.JumpHostId ?? "", "environment" => h.Environment,
        "profile" => h.TerminalProfileId ?? "", "vpn" => h.VpnProfileId ?? "", "tmux" => h.Tmux, _ => "",
    };

    /// <summary>Whether the field means anything for this host (a jump host and tmux are SSH things; Remote Desktop and VNC
    /// sign in with a password, never a key; Telnet types nothing for you), and the value isn't the host itself.</summary>
    public static bool Fits(HostEntry h, string field, string value) => field switch
    {
        "jump" => h.IsSsh && value != h.Id,
        "tmux" => h.IsSsh,
        "credential" => !h.IsTelnet && (h.IsSsh || value.Length == 0 || AppData.Current.Credentials.FirstOrDefault(c => c.Id == value) is not { Kind: CredentialKind.PrivateKey }),
        _ => true,
    };

    private static bool SetHost(HostEntry h, string field, string value)
    {
        if (HostValue(h, field) == value) return false;
        string? Id(string v) => v.Length == 0 ? null : v;
        switch (field)
        {
            case "user": h.Username = value; break;
            case "credential":
                h.CredentialId = Id(value);
                // The folder's credential replaces a direct Bitwarden / Password Safe link (which would otherwise win), and
                // the sign-in follows its kind.
                h.BitwardenItem = ""; h.PasswordSafeAccount = "";
                if (AppData.Current.Credentials.FirstOrDefault(c => c.Id == value) is { } c && h.IsSsh)
                    h.Auth = c.Kind == CredentialKind.PrivateKey ? AuthMethod.PrivateKey : h.Auth == AuthMethod.KeyboardInteractive ? AuthMethod.KeyboardInteractive : AuthMethod.Password;
                break;
            case "jump": h.JumpHostId = Id(value); break;
            case "environment": h.Environment = Envs.Normalize(value); break;
            case "profile": h.TerminalProfileId = Id(value); break;
            case "vpn": h.VpnProfileId = Id(value); break;
            case "tmux": h.Tmux = value is "on" or "off" ? value : ""; break;
            default: return false;
        }
        return true;
    }

    /// <summary>Writes the folder's value onto every field each host follows. A field no folder sets any more stays as it
    /// is and becomes the host's own. Shared library hosts are never touched. Returns how many hosts changed.</summary>
    public static int Apply(IEnumerable<HostEntry> hosts)
    {
        int changed = 0;
        foreach (var h in hosts)
        {
            if (h.IsShared || h.IsAdHoc) continue;
            h.Inherits ??= new();
            if (h.Inherits.Count == 0) continue;
            bool any = false;
            foreach (var f in h.Inherits.ToList())
            {
                if (Effective(h.Group, f) is not { } eff || !Fits(h, f, eff.Value)) { h.Inherits.Remove(f); continue; }
                any |= SetHost(h, f, eff.Value);
            }
            if (any) changed++;
        }
        return changed;
    }

    /// <summary>The hosts in a folder and its sub-folders (this store's own).</summary>
    public static List<HostEntry> HostsUnder(string folder)
    {
        var path = AppData.NormalizeFolder(folder);
        return AppData.Current.Hosts.Where(h => !h.IsShared && AppData.InFolder(AppData.NormalizeFolder(h.Group), path)).ToList();
    }

    /// <summary>Makes hosts follow <paramref name="fields"/> of their folders (where a folder sets them and they fit), then
    /// applies. Their own values for those fields are replaced.</summary>
    public static int Adopt(IEnumerable<HostEntry> hosts, IEnumerable<string> fields)
    {
        var list = hosts.ToList();
        var wanted = fields.ToList();
        foreach (var h in list)
        {
            h.Inherits ??= new();
            foreach (var f in wanted)
                if (!h.Inherits.Contains(f) && Effective(h.Group, f) is { } eff && Fits(h, f, eff.Value)) h.Inherits.Add(f);
        }
        return Apply(list);
    }

    /// <summary>The host editor's rule: a field follows its folder while it has the folder's value; anything else is the
    /// host's own.</summary>
    public static void Decide(HostEntry h)
    {
        h.Inherits ??= new();
        foreach (var f in Fields)
        {
            bool follow = Effective(h.Group, f) is { } eff && Fits(h, f, eff.Value) && Same(f, HostValue(h, f), eff.Value);
            if (follow && !h.Inherits.Contains(f)) h.Inherits.Add(f);
            else if (!follow) h.Inherits.Remove(f);
        }
    }

    private static bool Same(string field, string a, string b) =>
        field == "environment" ? Envs.Normalize(a) == Envs.Normalize(b) : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>A folder renamed or moved: its defaults (and its sub-folders') move with it.</summary>
    public static void Renamed(string from, string to)
    {
        from = AppData.NormalizeFolder(from); to = AppData.NormalizeFolder(to);
        foreach (var s in All)
        {
            var p = AppData.NormalizeFolder(s.Path);
            if (AppData.InFolder(p, from)) s.Path = to + p[from.Length..];
        }
    }

    /// <summary>A folder deleted: its defaults and its sub-folders' go with it (its hosts moved up keep their values).</summary>
    public static void Deleted(string folder)
    {
        var path = AppData.NormalizeFolder(folder);
        All.RemoveAll(s => AppData.InFolder(AppData.NormalizeFolder(s.Path), path));
    }

    /// <summary>"user ops, jump host bastion-01 (from Customers/Contoso)" for the editor and the folder's tooltip.</summary>
    public static string Describe(string group, Func<string, string, string> show)
    {
        var parts = new List<string>();
        foreach (var f in Fields)
            if (Effective(group, f) is { } eff) parts.Add($"{Title(f)} {show(f, eff.Value)}" + (AppData.NormalizeFolder(group).Equals(eff.Folder, StringComparison.OrdinalIgnoreCase) ? "" : $" (from {eff.Folder})"));
        return string.Join(", ", parts);
    }
}
