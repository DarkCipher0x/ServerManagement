using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace DcxManager.Net;

/// <summary>
/// Uses Nmap when it is installed (0.3.0 §7): it can't be bundled (its licence doesn't allow it in a closed app), so the
/// app only looks for nmap.exe in its usual places and on PATH. Runs a TCP connect scan (-sT: no admin, no driver), with
/// service/version detection (-sV) when asked, against the addresses the scope guard already expanded (never the raw
/// text), and reads its XML output. SYN scans and OS detection need Nmap + Npcap and admin: run Nmap yourself for those.
/// </summary>
public static class NmapRunner
{
    public static string? Find()
    {
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var p = Path.Combine(dir, "Nmap", "nmap.exe");
            if (dir.Length > 0 && File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var p = Path.Combine(dir.Trim(), "nmap.exe"); if (dir.Trim().Length > 0 && File.Exists(p)) return p; } catch { }
        return null;
    }

    /// <summary>Runs Nmap; each open port is reported. Returns Nmap's summary line (or why it failed).</summary>
    public static async Task<string> RunAsync(IReadOnlyList<string> hosts, IReadOnlyList<int> ports, bool versions, IProgress<OpenPort> found, CancellationToken ct)
    {
        var exe = Find() ?? throw new InvalidOperationException("Nmap isn't installed (nmap.org). The built-in scanner works without it.");
        Services.Restrictions.Demand("network.scan", "you");   // 0.5.1: Group Policy can turn scanning off
        if (hosts.Count > 0) Services.Offline.EnforceStatic(hosts[0], "an Nmap scan");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-sT", "-Pn", "--open", "-T4", "-oX", "-", "-p", Compact(ports) }) psi.ArgumentList.Add(a);
        if (versions) psi.ArgumentList.Add("-sV");
        foreach (var h in hosts) psi.ArgumentList.Add(h);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var xml = await outTask;
        if (p.ExitCode != 0) throw new InvalidOperationException("Nmap: " + (await errTask).Trim());
        var doc = XDocument.Parse(xml);
        foreach (var host in doc.Descendants("host"))
        {
            var addr = host.Elements("address").FirstOrDefault(a => (string?)a.Attribute("addrtype") is "ipv4" or "ipv6")?.Attribute("addr")?.Value ?? "";
            foreach (var port in host.Descendants("port").Where(x => x.Element("state")?.Attribute("state")?.Value == "open"))
            {
                var svc = port.Element("service");
                var detail = string.Join(" ", new[] { svc?.Attribute("product")?.Value, svc?.Attribute("version")?.Value, svc?.Attribute("extrainfo")?.Value }.Where(x => !string.IsNullOrEmpty(x)));
                found.Report(new OpenPort(addr, int.Parse(port.Attribute("portid")!.Value), svc?.Attribute("name")?.Value ?? "", "nmap" + (detail.Length > 0 ? ": " + detail : ""), 0));
            }
        }
        return doc.Root?.Element("runstats")?.Element("finished")?.Attribute("summary")?.Value ?? "done";
    }

    /// <summary>[22, 80, 81, 82, 443] → "22,80-82,443".</summary>
    internal static string Compact(IReadOnlyList<int> ports)
    {
        var parts = new List<string>();
        for (int i = 0; i < ports.Count;)
        {
            int j = i;
            while (j + 1 < ports.Count && ports[j + 1] == ports[j] + 1) j++;
            parts.Add(i == j ? ports[i].ToString() : $"{ports[i]}-{ports[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
