using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.4: quick connect — every protocol's line parses back to itself, a prefix typed with SSH selected wins,
/// pins persist in the encrypted store and lead the dropdown, Recent follows (without pinned duplicates), saved hosts
/// match what is typed, and the dropdown is built without ever opening a popup. TEST-NET addresses only.
/// </summary>
public static class QuickConnectTests
{
    public static Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var d = AppData.Current;
        var (pinsWas, recentWas) = (d.PinnedQuick.ToList(), d.RecentAdHoc.ToList());
        var host = new HostEntry { Name = "quick-web-01", Address = "192.0.2.44", Username = "ops", Group = "Quick/Test" };
        try
        {
            var cases = new (string Proto, string Target, string Line)[]
            {
                ("ssh", "ops@192.0.2.10:2222", "ops@192.0.2.10:2222"),
                ("sftp", "ops@192.0.2.10", "sftp://ops@192.0.2.10"),
                ("rdp", "Administrator@192.0.2.20", "rdp://Administrator@192.0.2.20"),
                ("telnet", "192.0.2.30 2323", "telnet 192.0.2.30 2323"),
                ("serial", "COM3 115200 8N1", "COM3 115200 8N1"),
                ("local", "wsl Ubuntu", "wsl Ubuntu"),
                ("web", "192.0.2.40:8443", "https://192.0.2.40:8443"),
                ("external", "ops@192.0.2.50", "ext://ops@192.0.2.50"),
            };
            foreach (var (proto, target, line) in cases)
            {
                if (QuickTargets.Canonical(proto, target) != line) throw new Exception($"{proto} {target} → {QuickTargets.Canonical(proto, target)}");
                var (p, t) = QuickTargets.Split(line);
                if (p != proto || (proto != "web" && t != target)) throw new Exception($"{line} split as {p} / {t}");
            }
            if (QuickTargets.Canonical("ssh", "rdp://admin@192.0.2.21") != "rdp://admin@192.0.2.21" || QuickTargets.Split("mstsc admin@192.0.2.21").Proto != "rdp")
                throw new Exception("a typed prefix didn't pick its protocol");
            if (QuickTargets.All(telnet: false).Any(x => x.Id == "telnet") || !QuickTargets.All(telnet: true).Any(x => x.Id == "telnet")) throw new Exception("telnet listed while not allowed");

            d.Hosts.Add(host);
            d.PinnedQuick.Clear(); d.RecentAdHoc.Clear();
            QuickTargets.Remember("sftp://ops@192.0.2.10");
            QuickTargets.Remember("COM3 115200 8N1");
            if (!QuickTargets.TogglePin("COM3 115200 8N1")) throw new Exception("pin");
            AppData.Load();   // pins and history come back from the encrypted store
            d = AppData.Current;
            if (d.PinnedQuick.FirstOrDefault() != "COM3 115200 8N1" || d.RecentAdHoc.Count != 2) throw new Exception("pins / recent not persisted");
            var rows = main.QuickRowsForTest("");
            var iPinned = rows.FindIndex(r => r.StartsWith("# 📌"));
            var iRecent = rows.FindIndex(r => r.StartsWith("# 🕘"));
            var iSaved = rows.FindIndex(r => r.StartsWith("# ▣"));
            if (iPinned != 0 || rows[1] != "📌 COM3 115200 8N1" || iRecent < 0 || iSaved < iRecent || rows.Count(r => r.EndsWith("COM3 115200 8N1")) != 1) throw new Exception("dropdown order: " + string.Join(" | ", rows));
            var filtered = main.QuickRowsForTest("quick-web");
            if (!filtered.Contains("quick-web-01") || filtered.Any(r => r.Contains("COM3"))) throw new Exception("filter: " + string.Join(" | ", filtered));
            if (QuickTargets.TogglePin("COM3 115200 8N1") || AppData.Current.PinnedQuick.Count != 0) throw new Exception("unpin");
            main.SetQuickProto("rdp");
            if (main.QuickProto != "rdp") throw new Exception("protocol menu");
            main.SetQuickProto("ssh");
            ok("quick connect: SSH, SFTP, Remote Desktop, Telnet (only while allowed), serial, local shell, web UI and external SSH lines round-trip; a typed prefix picks its protocol; pins persist encrypted and lead the dropdown, then Recent (no duplicates), then matching saved hosts; filter by typing; built without opening a popup");
        }
        catch (Exception ex) { fail("quick connect", ex); }
        finally
        {
            var cur = AppData.Current;
            cur.Hosts.RemoveAll(h => h.Name == "quick-web-01");
            cur.PinnedQuick = pinsWas; cur.RecentAdHoc = recentWas;
            AppData.Save();
        }
        return Task.CompletedTask;
    }
}
