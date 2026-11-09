using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DcxManager.Addons;
using DcxManager.Views;

namespace DcxManager.Services;

/// <summary>
/// Loads module add-ons at start-up (ADR-120). For each enabled add-on with a module: Group Policy and the add-on rules
/// allow it, its installed files still match what was approved, its module.json still verifies with the release keys and
/// lists exactly those files, and the module API matches. Then the assembly is loaded from its (decrypted) bytes into a
/// load context of its own — the app's assemblies are shared from the app, so the module sees the same IAppModule,
/// AppSettings and screens — the named type is created and registered as a module. Any failure is written to the log and
/// the audit log and leaves the app running without that module; a tampered package is also disabled.
/// </summary>
public static class ModuleAddons
{
    public sealed record Loaded(InstalledAddon Addon, IAppModule Module, Assembly Assembly);
    public sealed record Skipped(string Id, string Why);

    private static readonly List<Loaded> LoadedList = new();
    private static readonly List<Skipped> SkippedList = new();

    public static IReadOnlyList<Loaded> All { get { lock (LoadedList) return LoadedList.ToList(); } }
    public static IReadOnlyList<Skipped> NotLoaded { get { lock (LoadedList) return SkippedList.ToList(); } }
    public static bool IsLoaded(string addonId) => All.Any(l => l.Addon.Id == addonId);

    /// <summary>Loads every enabled module add-on. Never throws.</summary>
    public static void LoadAll()
    {
        foreach (var a in AddonPackages.All.Where(a => a.Manifest.Module is not null).ToList())
        {
            try
            {
                var why = Check(a, out var tampered);
                if (why is not null)
                {
                    lock (LoadedList) SkippedList.Add(new(a.Id, why));
                    if (tampered && a.Enabled) AddonPackages.SetEnabled(a, false, why);
                    else if (a.Enabled) AuditLog.Write("app", "addons", "module add-on not loaded", a.Id + " · " + why);
                    continue;
                }
                var (module, asm) = Create(a);
                AppModules.Add(module);
                lock (LoadedList) LoadedList.Add(new(a, module, asm));
                AuditLog.Write("app", "addons", "module add-on loaded", $"{a.Id} {a.Manifest.Version} · {a.Manifest.Module!.Type}");
            }
            catch (Exception ex)
            {
                Log.Write("Module add-on " + a.Id, ex);
                lock (LoadedList) SkippedList.Add(new(a.Id, "it failed to load: " + ex.GetBaseException().Message));
                AuditLog.Write("app", "addons", "module add-on failed to load", a.Id + " · " + ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>Why the add-on's module isn't loaded now, or null. <paramref name="tampered"/>: its files or signature no
    /// longer match (then it's disabled), as opposed to a rule that may change.</summary>
    public static string? Check(InstalledAddon a, out bool tampered)
    {
        tampered = false;
        if (a.Manifest.Module is not { } mod) return "it isn't a module add-on";
        if (!a.Enabled) return "it is turned off";
        if ((AddonPackages.ModuleRefusal(a.Manifest) ?? AddonPackages.UseRefusal(a)) is { } rule) return rule;
        if (mod.Api != AddonModuleInfo.CurrentApi) return $"it is made for module API {mod.Api}; this app provides {AddonModuleInfo.CurrentApi}";
        if (AddonPackages.AppTooOld(a.Manifest.MinAppVersion, AppInfo.Version) is { } old) return old;   // 0.6.43: e.g. the app was rolled back
        if (AddonPackages.VerifyFiles(a) is { } changed) { tampered = true; return "its files changed: " + changed; }
        byte[]? Read(string rel)
        {
            if (!a.Files.Keys.Any(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase))) return null;
            return AddonPackages.ReadInstalled(a, rel);
        }
        if (ModuleSignature.Problem(a.Manifest, a.Files.Keys.ToList(), Read) is { } sig) { tampered = true; return sig; }
        return null;
    }

    private static (IAppModule, Assembly) Create(InstalledAddon a)
    {
        var mod = a.Manifest.Module!;
        var ctx = new PackageContext(a);
        var asm = ctx.LoadFromStream(new MemoryStream(AddonPackages.ReadInstalled(a, mod.Assembly)));
        var type = asm.GetType(mod.Type, throwOnError: false) ?? throw new InvalidDataException($"{mod.Assembly} has no type {mod.Type}");
        if (!typeof(IAppModule).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException($"{mod.Type} isn't a module (IAppModule)");
        var module = (IAppModule)(Activator.CreateInstance(type) ?? throw new InvalidDataException("couldn't create " + mod.Type));
        if (!ModuleSettings.ValidId(module.Id)) throw new InvalidDataException("the module's id isn't valid: " + module.Id);
        if (AppModules.All.Any(m => m.Id == module.Id)) throw new InvalidDataException("a module with the id " + module.Id + " is already loaded");
        if (module is IAddonModule am) am.Attach(new Package(a));
        return (module, asm);
    }

    /// <summary>The module's assemblies: the app's own are shared (the default context answers first); the package's other
    /// .dll files are loaded from their checked bytes.</summary>
    private sealed class PackageContext : AssemblyLoadContext
    {
        private readonly InstalledAddon _a;
        public PackageContext(InstalledAddon a) : base("addon:" + a.Id, isCollectible: false)
        {
            _a = a;
            Resolving += (ctx, name) =>
            {
                var rel = _a.Files.Keys.FirstOrDefault(k => string.Equals(Path.GetFileName(k), name.Name + ".dll", StringComparison.OrdinalIgnoreCase));
                return rel is null ? null : ctx.LoadFromStream(new MemoryStream(AddonPackages.ReadInstalled(_a, rel)));
            };
        }
        protected override Assembly? Load(AssemblyName name) => null;   // default first: shared types stay the app's
    }

    private sealed class Package(InstalledAddon a) : IAddonPackage
    {
        public string Id => a.Id;
        public string Version => a.Manifest.Version;
        public byte[] Read(string relativePath) => AddonPackages.ReadInstalled(a, relativePath);
        public string ReadText(string relativePath) => AddonPackages.ReadInstalledText(a, relativePath);
        public string DataFolder => AddonPackages.DataFolder(a.Id);
    }

    /// <summary>Self-test (a scratch store only): installs these packages as the user would, through Inspect and Install,
    /// before the modules load. A refused package is logged and left out, so the self-test reports it as not loaded.</summary>
    internal static void InstallForSelfTest(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            try
            {
                var info = AddonPackages.Inspect(p);
                if (info.Refusal is { } why) { lock (LoadedList) SkippedList.Add(new(info.Manifest.Id is { Length: > 0 } id ? id : Path.GetFileName(p), "refused at install: " + why)); continue; }
                AddonPackages.Install(info, Array.Empty<string>());
            }
            catch (Exception ex) { Log.Write("Self-test add-on " + p, ex); lock (LoadedList) SkippedList.Add(new(Path.GetFileName(p), "install failed: " + ex.Message)); }
    }

    /// <summary>Self-test: loads a checked package's module without registering it (proves the path, leaves the app alone).</summary>
    internal static IAppModule CreateForTest(InstalledAddon a)
    {
        if (Check(a, out _) is { } why) throw new InvalidOperationException(why);
        return Create(a).Item1;
    }
}
