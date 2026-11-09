using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Addons;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-120 (module add-ons). With throwaway test keys standing in for the release keys: a signed module package is
/// inspected as signed, installed, checked and its assembly loaded from its decrypted bytes into a context of its own; a
/// changed file, an unlisted file, another purpose, another module API, a missing post-quantum signature or the policy
/// AddonAllowModules = 0 are refused; with the pinned release keys back, the test-signed package is refused. Module
/// settings move out of the app's settings and are saved with them; a module can't make a secret a policy. Then every
/// loaded module add-on's own self-test runs (the stream manager's in the run that installs it); with none loaded,
/// nothing of the stream manager exists. No window or dialog.
/// </summary>
internal static class ModuleAddonTests
{
    public static async Task RunAsync(MainWindow main, string scratch, Action<string> ok, Action<string, Exception?> fail)
    {
        try { ok("module add-on signatures (ADR-120): " + Signatures(scratch)); }
        catch (Exception ex) { fail("module add-on signatures", ex); }
        finally { Updater.TestKeys = null; }

        try { ok("module settings (ADR-120): " + Settings()); }
        catch (Exception ex) { fail("module settings", ex); }

        int coreApi = AddonModuleInfo.CurrentApi, uiApi = ModuleAddonApi.CurrentApi;   // locals: checked at run time, no CS0162
        if (coreApi != uiApi) fail("module API", new Exception($"AddonModuleInfo {AddonModuleInfo.CurrentApi} vs ModuleAddonApi {ModuleAddonApi.CurrentApi}"));

        var loaded = ModuleAddons.All;
        foreach (var l in loaded)
        {
            if (l.Module is not IModuleSelfTest t) { fail("module add-on " + l.Addon.Id, new Exception("it has no self-test (IModuleSelfTest)")); continue; }
            try { await t.RunAsync(main, ok, fail); }
            catch (Exception ex) { fail("module add-on " + l.Addon.Id + " self-test", ex); }
        }
        foreach (var n in ModuleAddons.NotLoaded) fail("module add-on " + n.Id, new Exception("not loaded: " + n.Why));

        if (!AppModules.IsLoaded("stream"))
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Dcx.Stream")) throw new Exception("Dcx.Stream loaded without its add-on");
                if (AppModules.NavItemsIn("STREAM").Any() || main.NavKeys.Any(k => k.StartsWith("stream", StringComparison.Ordinal))) throw new Exception("stream sidebar items without the add-on");
                if (Services.Keys.KeyCommands.All.Any(c => c.Id.StartsWith("stream.", StringComparison.Ordinal) || c.Id.StartsWith("streamerbot.", StringComparison.Ordinal))) throw new Exception("stream commands without the add-on");
                if (Services.DashboardSources.All.Any(s => s.Module == "stream") || Services.PrivacyRows.All.Any(p => p.Module == "stream")) throw new Exception("stream dashboard/privacy rows without the add-on");
                if (HelpKb.Articles.Any(a => a.Module == "stream") || PolicySettings.Catalog.Any(e => e.Name.StartsWith("Stream", StringComparison.Ordinal))) throw new Exception("stream help or policies without the add-on");
                ok($"stream manager not installed (ADR-120): Dcx.Stream isn't in the app and isn't loaded; no STREAM section, command, dashboard source, privacy row, help article or policy exists; {loaded.Count} module add-on(s) loaded");
            }
            catch (Exception ex) { fail("stream manager not installed", ex); }
        }
    }

    private static byte[] Sdk(string name) { using var rs = typeof(App).Assembly.GetManifestResourceStream("sdk/" + name)!; using var ms = new MemoryStream(); rs.CopyTo(ms); return ms.ToArray(); }

    private static string Signatures(string scratch)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kp = ec.ExportParameters(false);
        var (seed, pub) = Pqc.SigGenerate();
        byte[] Sig(byte[] d) => Encoding.ASCII.GetBytes(Convert.ToBase64String(ec.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        byte[] PqSig(byte[] d) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(seed, d)));
        Updater.TestKeys = (Convert.ToHexString(kp.Q.X!), Convert.ToHexString(kp.Q.Y!), Convert.ToBase64String(pub));

        var dir = Path.Combine(scratch, "module-addons");
        Directory.CreateDirectory(dir);
        var dll = Sdk("DcxBridge.dll");   // any assembly: the test proves the path, not a module
        const string Id = "com.example.selftest-module";
        string Manifest(int api = AddonModuleInfo.CurrentApi) =>
            """{"api":2,"id":"com.example.selftest-module","name":"Self-test module","version":"1.0.0","publisher":"Self-test","description":"a test module","module":{"assembly":"Test.Module.dll","type":"DcxManager.SelfTestModule.NoSuchModule","api":""" + api + "}}";
        string Pack(string name, Action<Dictionary<string, byte[]>>? change = null, Func<byte[], byte[]>? mj = null, int api = AddonModuleInfo.CurrentApi, bool pq = true)
        {
            var files = new Dictionary<string, byte[]> { ["addon.json"] = Encoding.UTF8.GetBytes(Manifest(api)), ["Test.Module.dll"] = dll, ["help.md"] = Encoding.UTF8.GetBytes("## Test\nbody\n") };
            var m = System.Text.Json.JsonSerializer.Deserialize<AddonManifest>(files["addon.json"], new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var json = ModuleSignature.Build(m, files.Select(kv => (kv.Key, kv.Value)));
            if (mj != null) json = mj(json);
            files[ModuleSignature.ManifestFile] = json;
            files[ModuleSignature.SigFile] = Sig(json);
            if (pq) files[ModuleSignature.PqSigFile] = PqSig(json);
            change?.Invoke(files);
            var path = Path.Combine(dir, name + ".dcxaddon");
            if (File.Exists(path)) File.Delete(path);
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (rel, bytes) in files) { using var es = z.CreateEntry(rel).Open(); es.Write(bytes); }
            return path;
        }
        byte[] Retarget(byte[] json, string key, JsonNode value) { var o = JsonNode.Parse(json)!.AsObject(); o[key] = value; return Encoding.UTF8.GetBytes(o.ToJsonString()); }

        var good = AddonPackages.Inspect(Pack("good"));
        if (good.Refusal != null || !good.Signature.StartsWith("Signed by DarkCipher Systems", StringComparison.Ordinal)) throw new Exception("a signed module: " + (good.Refusal ?? good.Signature));
        void Refused(string what, string path, string expect)
        {
            var i = AddonPackages.Inspect(path);
            if (i.Refusal is null || !i.Refusal.Contains(expect, StringComparison.OrdinalIgnoreCase)) throw new Exception($"{what}: {i.Refusal ?? "accepted"}");
        }
        Refused("a changed assembly", Pack("tampered", f => { var b = (byte[])f["Test.Module.dll"].Clone(); b[^1] ^= 1; f["Test.Module.dll"] = b; }), "isn't the one that was signed");
        Refused("an unlisted file", Pack("extra", f => f["extra.txt"] = "x"u8.ToArray()), "aren't the ones that were signed");
        Refused("another purpose", Pack("purpose", mj: j => Retarget(j, "purpose", "dcx-update")), "isn't a module signature");
        Refused("another version", Pack("version", mj: j => Retarget(j, "version", "9.9.9")), "another add-on or version");
        Refused("another module API", Pack("api", api: AddonModuleInfo.CurrentApi + 1), "module API");
        Refused("no post-quantum signature", Pack("nopq", pq: false), "post-quantum");
        Refused("a signature by another key", Pack("otherkey", f => { using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256); f[ModuleSignature.SigFile] = Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(f[ModuleSignature.ManifestFile], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))); }), "release key");
        Refused("no signature", Pack("unsigned", f => { f.Remove(ModuleSignature.SigFile); f.Remove(ModuleSignature.PqSigFile); }), "module.json");
        var s = AppSettings.Current;
        s.AddonAllowModules = false;
        try { Refused("AddonAllowModules = 0", good.Path, "AddonAllowModules"); }
        finally { s.AddonAllowModules = true; }

        // Installed: sealed at rest, checked, and loaded from its bytes into its own context (the type doesn't exist: refused there).
        var a = AddonPackages.Install(AddonPackages.Inspect(good.Path), Array.Empty<string>());
        try
        {
            if (!a.Sealed) throw new Exception("the module package isn't encrypted at rest");
            if (ModuleAddons.Check(a, out _) is { } why) throw new Exception("installed module not loadable: " + why);
            try { ModuleAddons.CreateForTest(a); throw new Exception("a missing module type was accepted"); }
            catch (InvalidDataException ex) when (ex.Message.Contains("has no type")) { }
            var ctx = AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name == "DcxBridge").Select(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext).FirstOrDefault(c => c?.Name == "addon:" + Id);
            if (ctx is null) throw new Exception("the assembly wasn't loaded into the add-on's own context");
            AddonPackages.SetEnabled(a, false);
            if (ModuleAddons.Check(a, out _) is null) throw new Exception("a disabled module would load");
            AddonPackages.SetEnabled(a, true);
            Updater.TestKeys = null;   // the pinned release keys: a test-signed module never loads
            if (ModuleAddons.Check(a, out bool tampered) is null || !tampered) throw new Exception("a module not signed by the release keys would load");
        }
        finally { AddonPackages.Remove(a); }
        return "a module package signed for its id, version, API and every file is inspected as signed by DarkCipher Systems, installed sealed and loaded from its bytes into its own load context; a changed or unlisted file, another purpose, version or module API, a missing post-quantum signature, another key, no signature and AddonAllowModules = 0 are refused; disabled or not signed by the release keys, it doesn't load";
    }

    private sealed class ZzTestSettings
    {
        public int ZzTestValue { get; set; } = 1;
        public string ZzTestToken { get; set; } = "";
    }

    private static string Settings()
    {
        var s = AppSettings.Current;
        s.Retired ??= new();
        s.Retired["ZzTestValue"] = System.Text.Json.JsonDocument.Parse("42").RootElement.Clone();
        s.Retired["ZzUnrelated"] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone();
        try
        {
            var t = ModuleSettings.Attach<ZzTestSettings>("zz-selftest");
            if (t.ZzTestValue != 42 || s.Retired.ContainsKey("ZzTestValue") || !s.Retired.ContainsKey("ZzUnrelated")) throw new Exception("an old app setting didn't move into the module");
            t.ZzTestValue = 7;
            AppSettings.Save();
            if (s.ModuleData.GetValueOrDefault("zz-selftest")?["ZzTestValue"]?.GetValue<int>() != 7) throw new Exception("module settings weren't saved with the app's");
            try { PolicySettings.RegisterModule("zz-selftest", "Zz", "ZzTest", typeof(ZzTestSettings), () => t, new Dictionary<string, string>()); throw new Exception("a secret became a policy"); }
            catch (ArgumentException ex) when (ex.Message.Contains("secret")) { }
            if (ModuleSettings.ValidId("Bad Id") || !ModuleSettings.ValidId("stream")) throw new Exception("module ids");
        }
        finally { ModuleSettings.DetachForTest("zz-selftest"); s.Retired.Remove("ZzUnrelated"); s.ModuleData.Remove("zz-selftest"); AppSettings.Save(); }
        return "values an older app kept move into the module's settings the first time it attaches (others stay untouched), module settings are saved with the app's encrypted settings, a setting that looks like a secret can't be a policy, module ids are checked";
    }
}
