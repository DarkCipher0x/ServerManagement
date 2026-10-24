using System.Net;
using DcxManager.Services;
using DcxManager.Vnc;

namespace DcxManager.Net;

/// <summary>One address that answered: its reverse-DNS name, the open ports with what answers, and the platform guess.</summary>
public sealed class DiscoveredHost
{
    public required string Address { get; init; }
    public string Name { get; set; } = "";
    public List<OpenPort> Ports { get; } = new();
    public string Platform { get; set; } = "";
    /// <summary>Why the platform was guessed (shown in the review grid).</summary>
    public string Evidence { get; set; } = "";

    public OpenPort? Port(int p) => Ports.FirstOrDefault(x => x.Port == p);
    public string ShortName => Name.Length == 0 ? Address : Name.Split('.')[0];
}

/// <summary>
/// Finds machines to add as hosts (0.5.1 #18): a TCP connect scan of the ports the app can open (SSH, Remote Desktop, VNC,
/// Telnet when it is allowed) on private addresses only, what answers on each (the SSH and RFB banners), the reverse-DNS
/// name, and a platform guess from those. Nothing is signed in to and nothing is written to a service. The review window
/// turns each one into host entries (SSH, RDP, VNC) that the user edits, ticks and imports.
/// </summary>
public static class HostDiscovery
{
    /// <summary>SSH (and the common alternative 2222), Remote Desktop, VNC displays :0–:3.</summary>
    public static readonly int[] DefaultPorts = { 22, 2222, 3389, 5900, 5901, 5902, 5903 };
    public const int TelnetPort = 23;

    public static IReadOnlyList<int> PortsToScan(bool telnet) => telnet ? DefaultPorts.Append(TelnetPort).ToArray() : DefaultPorts;

    /// <summary>Scans <paramref name="targets"/> (addresses, ranges or CIDR, private only — <see cref="NetTargets.Parse"/>).</summary>
    public static async Task<List<DiscoveredHost>> ScanAsync(string targets, IReadOnlyList<int> ports, IProgress<int> progress, CancellationToken ct, int timeoutMs = 700)
    {
        Restrictions.Demand("network.scan", "you");
        var parsed = NetTargets.Parse(targets);
        if (parsed.PublicSingles.Count > 0) throw new ArgumentException($"{parsed.PublicSingles[0]} isn't a private address: host import scans only your own networks.");
        var hosts = parsed.Addresses.Select(a => a.ToString()).Concat(parsed.Names).ToList();
        if (hosts.Count == 0) throw new ArgumentException("No addresses to scan.");
        var found = new Dictionary<string, DiscoveredHost>(StringComparer.OrdinalIgnoreCase);
        void Add(OpenPort p) { lock (found) { if (!found.TryGetValue(p.Address, out var d)) found[p.Address] = d = new DiscoveredHost { Address = p.Address }; d.Ports.Add(p); } }
        await PortScanner.RunAsync(hosts, ports, new SyncProgress<OpenPort>(Add), progress, ct, parallel: 128, timeoutMs: timeoutMs, detect: true);
        var list = found.Values.ToList();
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(list.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var dns = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dns.CancelAfter(1500);
                if (IPAddress.TryParse(d.Address, out _)) d.Name = (await Dns.GetHostEntryAsync(d.Address, dns.Token)).HostName;
                else d.Name = d.Address;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException) { }
            finally { gate.Release(); }
        }));
        foreach (var d in list) { d.Ports.Sort((a, b) => a.Port.CompareTo(b.Port)); Guess(d); }
        return list.OrderBy(d => IPAddress.TryParse(d.Address, out var ip) ? ip.GetAddressBytes().Aggregate(0L, (n, b) => n * 256 + b) : long.MaxValue).ThenBy(d => d.Address).ToList();
    }

    /// <summary>The platform from what answered: SSH banners name the distribution or the device, Apple's VNC answers
    /// RFB 003.889, Remote Desktop without SSH is Windows.</summary>
    public static void Guess(DiscoveredHost d)
    {
        var ssh = d.Ports.FirstOrDefault(p => p.Service == "SSH")?.Detail ?? "";
        var rfb = d.Ports.FirstOrDefault(p => p.Service == "VNC")?.Detail ?? "";
        bool rdp = d.Port(3389) != null;
        (d.Platform, d.Evidence) =
            rfb.Contains("003.889", StringComparison.Ordinal) ? ("macos", "Apple Screen Sharing (RFB 003.889)")
            : Has(ssh, "Windows") ? ("windows", "OpenSSH for Windows")
            : Has(ssh, "dropbear", "Cisco", "ROSSSH", "HUAWEI", "Comware", "FortiSSH", "RomSShell", "SSH-2.0--", "Mocana", "lancom") ? ("network", "SSH banner of a network device: " + ssh)
            : Has(ssh, "Ubuntu", "Debian", "Raspbian", "el7", "el8", "el9", "fc3", "FreeBSD", "OpenBSD", "NetBSD", "Alpine") ? (ssh.Contains("BSD", StringComparison.Ordinal) ? "other" : "linux", ssh)
            : ssh.Length > 0 ? ("linux", ssh)
            : rdp ? ("windows", "Remote Desktop, no SSH")
            : rfb.Length > 0 ? ("linux", "VNC " + rfb)
            : ("other", string.Join(", ", d.Ports.Select(p => $"{p.Port} {p.Service}")));
    }

    private static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>The host entries one discovered machine can become: SSH, Remote Desktop, VNC (a Mac's Screen Sharing is
    /// tunnelled through its SSH when that answers too) and Telnet when allowed. Nothing is saved here.</summary>
    public static List<HostEntry> Candidates(DiscoveredHost d, bool telnetAllowed, string folder)
    {
        var list = new List<HostEntry>();
        var name = d.ShortName;
        var env = "";
        HostEntry Seed(string protocol, int port, string suffix) => new()
        {
            Name = name + suffix, Address = d.Address, Port = port, Protocol = protocol, Group = folder, Environment = env,
            Platform = d.Platform is "linux" or "windows" or "macos" or "network" or "other" ? d.Platform : "",
            Tags = "discovered",
        };
        var sshPort = d.Ports.FirstOrDefault(p => p.Service == "SSH");
        if (sshPort != null)
        {
            var h = Seed("ssh", sshPort.Port, "");
            h.Username = d.Platform == "windows" ? "Administrator" : AppSettings.Current.DefaultUsername;
            h.Auth = AuthMethod.PrivateKey;
            list.Add(h);
        }
        if (d.Port(3389) != null)
        {
            var h = Seed("rdp", 3389, sshPort != null ? " (Remote Desktop)" : "");
            h.Username = "Administrator"; h.Auth = AuthMethod.Password;
            if (h.Platform.Length == 0) h.Platform = "windows";
            list.Add(h);
        }
        foreach (var v in d.Ports.Where(p => p.Service == "VNC" || p.Port is >= 5900 and <= 5903 && p.Detail.StartsWith("RFB ", StringComparison.Ordinal)))
        {
            bool mac = d.Platform == "macos";
            var h = Seed("vnc", v.Port, sshPort != null || d.Port(3389) != null ? " desktop" + (v.Port != VncConnector.DefaultPort ? $" :{v.Port - 5900}" : "") : "");
            h.Username = ""; h.Auth = AuthMethod.Password;
            h.Vnc = new VncSettings { Kind = mac ? "mac" : "vnc", Through = sshPort != null ? "ssh" : "direct" };
            list.Add(h);
        }
        if (telnetAllowed && d.Port(TelnetPort) != null && sshPort == null)
        {
            var h = Seed("telnet", TelnetPort, "");
            h.Username = ""; h.Auth = AuthMethod.Password;
            list.Add(h);
        }
        return list;
    }

    /// <summary>True when a saved host already opens this address, protocol and port.</summary>
    public static bool AlreadySaved(HostEntry candidate) =>
        AppData.Current.Hosts.Any(h => !h.IsAdHoc && h.Address.Equals(candidate.Address, StringComparison.OrdinalIgnoreCase) && h.Protocol == candidate.Protocol && h.Port == candidate.Port);

    /// <summary>The identification clue for the AI: name, ports and banners only.</summary>
    public static HostClue Clue(DiscoveredHost d) => new(d.Address, d.Name, d.Ports.Select(p => new HostCluePort(p.Port, p.Service, p.Detail.Length > 160 ? p.Detail[..160] : p.Detail)).ToList());

    /// <summary>An IProgress that runs the callback on the reporting thread (Progress&lt;T&gt; would post to the UI).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
