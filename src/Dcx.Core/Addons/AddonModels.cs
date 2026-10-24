namespace DcxManager.Addons;

// The add-on records kept in the encrypted settings (moved out of AddonPackages.cs in 0.2.0 phase 6: they are stored
// data, so they live with the store; the package code that fills them stays with the add-ons).

public sealed class AddonCommand
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>global | host | terminal</summary>
    public string Context { get; set; } = "global";
}

public sealed class AddonManifest
{
    public int Api { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string Entry { get; set; } = "";
    /// <summary>A theme file in the package (ADR-030). A theme-only add-on has no entry, commands or permissions: nothing runs.</summary>
    public string Theme { get; set; } = "";
    /// <summary>A browser.json in the package (ADR-032): the portal browser add-on. Data only, like a theme.</summary>
    public string Browser { get; set; } = "";
    /// <summary>A library.json in the package (ADR-033): a shared library of hosts and team credentials. Data only; the
    /// encryption, sync and merging run in the app, never in an add-on process.</summary>
    public string Library { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public List<string> Permissions { get; set; } = new();
    public List<AddonCommand> Commands { get; set; } = new();
}

/// <summary>An installed add-on, as recorded in the encrypted settings (ADR-012).</summary>
public sealed class InstalledAddon
{
    public AddonManifest Manifest { get; set; } = new();
    /// <summary>Permissions the user approved (always a subset of the manifest's).</summary>
    public List<string> Granted { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Install folder (…\addons\&lt;id&gt;\&lt;version&gt;).</summary>
    public string Folder { get; set; } = "";
    /// <summary>Relative path → SHA-256 of every installed file; verified before every run.</summary>
    public Dictionary<string, string> Files { get; set; } = new();
    public string PackageSha256 { get; set; } = "";
    /// <summary>"Signed by …" / "NOT signed" for the entry program, at install time.</summary>
    public string Signature { get; set; } = "";
    public DateTime Installed { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public string? DisabledReason { get; set; }
    /// <summary>The installed files are sealed with the store key (data-only add-ons since 0.1.17, ADR-035): read them only
    /// through <see cref="AddonPackages.ReadInstalled"/>.</summary>
    public bool Sealed { get; set; }
    public List<string> RunLog { get; set; } = new();

    public string Id => Manifest.Id;
    public string Title => $"{Manifest.Name} {Manifest.Version}";
}
