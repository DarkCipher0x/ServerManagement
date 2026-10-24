using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>
/// User profiles (0.3.0 §23): several profiles in one installation, each with its own encrypted store (its own data key,
/// hosts, vault, settings, notes, team membership) — nothing is shared between them. The first profile is the store the
/// app always had ("default", %APPDATA%\DarkCipher Systems\ServerManagement); more live under profiles\&lt;id&gt;. The index
/// (profiles.json) holds names and kinds only. A profile is "local" or bound to a directory sign-in (Windows / Active
/// Directory, LDAPS, Microsoft Entra ID) that must succeed before its store opens.
/// </summary>
public sealed class ProfileInfo
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "";
    /// <summary>"local" or "directory".</summary>
    public string Kind { get; set; } = "local";
    /// <summary>The directory binding of a directory profile (kind, server, who must sign in); null for a local one.</summary>
    public SignIn.DirectoryBinding? Directory { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastUsed { get; set; }
    /// <summary>An accent for the picker (a brush key name or empty).</summary>
    public string Colour { get; set; } = "";

    public string KindText => Kind == "directory" && Directory != null ? Directory.Describe() : "local profile";
}

public sealed class ProfileIndex
{
    public int Version { get; set; } = 1;
    public List<ProfileInfo> Profiles { get; set; } = new();
    /// <summary>Open the last used profile without asking (one profile always opens straight away).</summary>
    public bool OpenLastWithoutAsking { get; set; }
    /// <summary>Open the profile bound to the signed-in Windows account without the picker (0.5.0; the computer policy
    /// AutoProfileForWindowsAccount overrides it).</summary>
    public bool AutoOpenForWindowsAccount { get; set; } = true;
    public string? LastUsed { get; set; }
}

public static class Profiles
{
    public const string DefaultId = "default";
    private static readonly Regex IdRx = new(@"^[a-z0-9]{8,32}$|^default$");

    /// <summary>Where the index and the extra profiles live: the home folder (self-test: beside its scratch store).</summary>
    public static string Root => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } d ? Path.GetFullPath(d).TrimEnd('\\') + "-profiles" : SecureStore.HomeDir;
    public static string IndexPath => Path.Combine(Root, "profiles.json");

    /// <summary>The profile this run opened (null before start-up chose one = the default store).</summary>
    public static ProfileInfo? Current { get; private set; }

    /// <summary>The app role the directory sign-in gave this run ("admin", "operator", "viewer") or "" for a local profile.</summary>
    public static string Role { get; internal set; } = "";
    public static bool ReadOnly => Role == "viewer";

    public static ProfileIndex Load()
    {
        try
        {
            if (File.Exists(IndexPath) && JsonSerializer.Deserialize<ProfileIndex>(File.ReadAllText(IndexPath), SecureStore.Json) is { } ix)
            {
                ix.Profiles = ix.Profiles.Where(p => IdRx.IsMatch(p.Id)).ToList();
                return ix;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log.Write("profiles index", ex); }
        return new ProfileIndex();
    }

    public static void Save(ProfileIndex ix)
    {
        Directory.CreateDirectory(Root);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ix, new JsonSerializerOptions(SecureStore.Json) { WriteIndented = true }));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    public static string DirFor(string id)
    {
        if (!IdRx.IsMatch(id)) throw new ArgumentException("profile id");
        if (id == DefaultId) return Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } d ? d : SecureStore.HomeDir;
        return Path.Combine(Root, "profiles", id);
    }

    /// <summary>The index with the default profile present (an existing install becomes the "default" profile, named after
    /// the Windows user, without any file moving).</summary>
    public static ProfileIndex LoadWithDefault()
    {
        var ix = Load();
        if (ix.Profiles.All(p => p.Id != DefaultId))
        {
            ix.Profiles.Insert(0, new ProfileInfo { Id = DefaultId, Name = Environment.UserName, Kind = "local" });
            try { Save(ix); } catch (IOException ex) { Log.Write("profiles index", ex); }
        }
        return ix;
    }

    public static ProfileInfo Create(string name, SignIn.DirectoryBinding? binding = null)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("A profile name of 1–60 characters.");
        var ix = LoadWithDefault();
        if (ix.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A profile with that name exists.");
        var p = new ProfileInfo { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), Name = name, Kind = binding == null ? "local" : "directory", Directory = binding };
        Directory.CreateDirectory(DirFor(p.Id));
        ix.Profiles.Add(p);
        Save(ix);
        return p;
    }

    public static void Rename(string id, string name)
    {
        var ix = LoadWithDefault();
        var p = ix.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("no such profile");
        p.Name = name.Trim();
        Save(ix);
    }

    /// <summary>Changes a directory profile's sign-in binding (0.5.0, Settings → Identity & directory). The binding is
    /// checked first; a local profile can't be turned into a directory one here (create a new one).</summary>
    public static void UpdateBinding(string id, SignIn.DirectoryBinding binding)
    {
        if (binding.Problem() is { } why) throw new ArgumentException(why);
        var ix = LoadWithDefault();
        var p = ix.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("no such profile");
        if (p.Kind != "directory") throw new InvalidOperationException("Only a profile that signs in with the organisation has a directory binding.");
        p.Directory = binding;
        Save(ix);
        if (Current?.Id == id) Current.Directory = binding;
    }

    /// <summary>The profile list's own "open the Windows account's profile automatically" switch.</summary>
    public static void SetAutoOpen(bool on) { var ix = LoadWithDefault(); ix.AutoOpenForWindowsAccount = on; Save(ix); }

    /// <summary>Removes a profile from the index and deletes its store folder (never the default one).</summary>
    public static void Delete(string id)
    {
        if (id == DefaultId) throw new InvalidOperationException("The first profile can't be deleted; remove the app's data instead.");
        var ix = LoadWithDefault();
        ix.Profiles.RemoveAll(p => p.Id == id);
        Save(ix);
        var dir = DirFor(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>Start-up: points the store at the chosen profile (before anything opens it) and remembers it.</summary>
    public static void Select(ProfileInfo p)
    {
        SecureStore.UseProfileDir(DirFor(p.Id));
        Current = p;
        var ix = LoadWithDefault();
        if (ix.Profiles.FirstOrDefault(x => x.Id == p.Id) is { } e) e.LastUsed = DateTime.Now;
        ix.LastUsed = p.Id;
        try { Save(ix); } catch (IOException ex) { Log.Write("profiles index", ex); }
    }

    /// <summary>The profile a command line asks for ("--profile &lt;id&gt;"), or null.</summary>
    public static string? Requested(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--profile" && IdRx.IsMatch(args[i + 1])) return args[i + 1];
        return null;
    }

    /// <summary>Machine policy (Group Policy): "RequireDirectorySignIn" forbids local profiles; read before any store opens.</summary>
    /// <summary>The directory profile bound to the account signed in to Windows (0.5.0, ADR-062), when automatic opening is
    /// on (computer policy first, then the profile list's own switch); null otherwise or when several match.</summary>
    public static ProfileInfo? ForWindowsAccount(ProfileIndex ix, SignIn.WindowsAccount.Account? account = null)
    {
        if (!(PolicySettings.MachineFlag("AutoProfileForWindowsAccount") ?? ix.AutoOpenForWindowsAccount)) return null;
        var a = account ?? SignIn.WindowsAccount.Current();
        var hits = ix.Profiles.Where(p => p.Kind == "directory" && p.Directory is { } b && SignIn.WindowsAccount.Matches(b, a)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    public static bool DirectoryRequiredByPolicy() => PolicySettings.MachineFlag("RequireDirectorySignIn") == true;
    public static bool LocalProfilesAllowed() => !DirectoryRequiredByPolicy();
}
