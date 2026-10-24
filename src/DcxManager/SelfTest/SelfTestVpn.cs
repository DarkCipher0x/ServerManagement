using System.Net;
using DcxManager.Services;
using DcxManager.Vpn;

namespace DcxManager;

/// <summary>
/// 0.3.0 §24: VPN tunnels without any real tunnel. WireGuard and OpenVPN configurations are imported (secrets split out,
/// commands refused or removed, routes read), rendered back (secrets joined, the app's route choice applied), the
/// routing-table read-back works, status output is parsed, the manager puts keys in the vault, and the "connect through"
/// hook brings a (fake) tunnel up before an SSH connection. No engine program runs.
/// </summary>
public static class VpnTests
{
    private const string WgKey = "yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=";
    private const string WgPub = "xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=";
    private const string WgPsk = "FpCyhws9cxwWoV4xELtfJvjJN+zQVRPISllRWgeopVE=";

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            var conf = $"[Interface]\nPrivateKey = {WgKey}\nAddress = 10.66.0.2/24\nDNS = 10.66.0.1\nPostUp = iptables -A FORWARD -i %i -j ACCEPT\n\n[Peer]\nPublicKey = {WgPub}\nPresharedKey = {WgPsk}\nAllowedIPs = 10.66.0.0/24, 192.168.50.0/24\nEndpoint = vpn.example.test:51820\nPersistentKeepalive = 25\n";
            var im = VpnConfig.Import(conf, "office.conf");
            if (im.Kind != "wireguard" || im.PrivateKey != WgKey || im.Psks?.Trim() != $"{WgPub} {WgPsk}" || im.Config.Contains(WgKey) || im.Config.Contains(WgPsk) || im.Config.Contains("PostUp", StringComparison.OrdinalIgnoreCase)) throw new Exception("wg import: " + im.Config);
            if (im.Routes.Count != 2 || im.RouteAll || im.Dns.Single() != "10.66.0.1" || !im.Warnings.Any(w => w.Contains("PostUp")) || im.SuggestedName != "office") throw new Exception("wg routes / warnings");
            var rendered = VpnConfig.RenderWireGuard(im.Config, im.PrivateKey!, im.Psks, new[] { "192.168.50.0/24" }, false, im.Dns);
            if (!rendered.Contains($"PrivateKey = {WgKey}") || !rendered.Contains($"PresharedKey = {WgPsk}") || !rendered.Contains("AllowedIPs = 192.168.50.0/24") || rendered.Contains("10.66.0.0/24") || !rendered.Contains("DNS = 10.66.0.1") || rendered.Contains("PostUp")) throw new Exception("wg render:\n" + rendered);
            if (!VpnConfig.RenderWireGuard(im.Config, im.PrivateKey!, null, Array.Empty<string>(), true, Array.Empty<string>()).Contains("AllowedIPs = 0.0.0.0/0, ::/0")) throw new Exception("route all");
            if (!VpnConfig.RenderWireGuard(im.Config, im.PrivateKey!, null, Array.Empty<string>(), false, Array.Empty<string>()).Contains("AllowedIPs = 10.66.0.0/24")) throw new Exception("tunnel network only");
            try { VpnConfig.Import("[Interface]\nPrivateKey = short\n[Peer]\nPublicKey = " + WgPub + "\n"); throw new Exception("a bad key was accepted"); } catch (ArgumentException) { }
            try { VpnConfig.Import($"[Interface]\nPrivateKey = {WgKey}\n[Peer]\nPublicKey = {WgPub}\nEndpoint = a;b:51820\n"); throw new Exception("a bad endpoint was accepted"); } catch (ArgumentException) { }

            var ovpn = "client\ndev tun\nproto udp\nremote vpn.example.test 1194\nauth-user-pass /etc/pw.txt\nroute 10.20.0.0 255.255.0.0\nredirect-gateway def1\ndhcp-option DNS 10.20.0.1\n<ca>\n-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n</ca>\n<cert>\n-----BEGIN CERTIFICATE-----\nMIIC\n-----END CERTIFICATE-----\n</cert>\n<key>\n-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n</key>\n"; // secret-scan: example
            var ov = VpnConfig.Import(ovpn, "work.ovpn");
            if (ov.Kind != "openvpn" || ov.PrivateKey?.Contains("MIIE") != true || ov.Config.Contains("<key>") || !ov.NeedsAuth || ov.Config.Contains("/etc/pw.txt") || ov.Routes.Single() != "10.20.0.0/16" || !ov.RouteAll || ov.Dns.Single() != "10.20.0.1") throw new Exception("ovpn import: " + ov.Config);
            var ro = VpnConfig.RenderOpenVpn(ov.Config, ov.PrivateKey, new[] { "10.20.0.0/16" }, false, ov.Dns, 7505);
            if (!ro.Contains("<key>\n-----BEGIN PRIVATE KEY-----") || !ro.Contains("route-nopull\nroute 10.20.0.0 255.255.0.0") || ro.Contains("redirect-gateway") || !ro.Contains("management 127.0.0.1 7505") || !ro.Contains("management-query-passwords") || !ro.Contains("script-security 0") || !ro.Contains("dhcp-option DNS 10.20.0.1")) throw new Exception("ovpn render:\n" + ro); // secret-scan: example
            foreach (var bad in new[] { "client\nremote x.test 1194\nup /tmp/evil.sh\n", "client\nremote x.test 1194\nscript-security 2\n", "client\nremote x.test 1194\nplugin foo.so\n", "client\nremote x.test 1194\nkey client.key\n", "client\nremote x.test 1194\nca ca.crt\n", "client\nremote a;b 1194\n" })
            {
                try { VpnConfig.Import(bad); throw new Exception("accepted: " + bad.Split('\n')[2]); } catch (ArgumentException) { }
            }

            if (VpnRoutes.Mask(24) != "255.255.255.0" || VpnRoutes.Mask(17) != "255.255.128.0" || VpnRoutes.MaskBits(IPAddress.Parse("255.255.255.192")) != 26 || VpnRoutes.Network(IPAddress.Parse("10.1.2.3"), 8) != "10.0.0.0") throw new Exception("masks");
            var parsed = VpnRoutes.Parse("10.0.0.0/8, 192.168.50.7/24;172.16.0.0/12");
            if (string.Join(" ", parsed) != "10.0.0.0/8 192.168.50.0/24 172.16.0.0/12") throw new Exception("route parse: " + string.Join(" ", parsed));
            try { VpnRoutes.Parse("10.0.0.0/33"); throw new Exception("a bad prefix was accepted"); } catch (ArgumentException) { }
            if (!VpnRoutes.Covers(parsed, IPAddress.Parse("192.168.50.200")) || VpnRoutes.Covers(parsed, IPAddress.Parse("8.8.8.8"))) throw new Exception("covers");
            var table = VpnRoutes.Table();
            if (table.Count == 0 || !table.Any(r => r.Prefix == "127.0.0.0" && r.Bits == 8)) throw new Exception("routing table read-back: " + table.Count + " routes");

            var wg = WireGuardEngine.ParseWgShow("interface: office\n  public key: x\n  listening port: 51820\n\npeer: " + WgPub + "\n  endpoint: 203.0.113.5:51820\n  allowed ips: 192.168.50.0/24\n  latest handshake: 1 minute, 12 seconds ago\n  transfer: 1.50 MiB received, 300.25 KiB sent\n");
            if (!wg.Up || wg.State != "up" || wg.Endpoint != "203.0.113.5:51820" || wg.RxBytes != 1572864 || wg.TxBytes != 307456 || wg.LastHandshake is not { } hs || (DateTime.Now - hs).TotalSeconds is < 60 or > 90) throw new Exception("wg show parse: " + wg);
            var ovs = OpenVpnEngine.ParseManagement("1761000000,CONNECTED,SUCCESS,10.20.0.6,203.0.113.9,1194,,\n", "TITLE,OpenVPN\nTUN/TAP read bytes,12345\nTUN/TAP write bytes,678\n");
            if (!ovs.Up || ovs.Endpoint != "203.0.113.9" || ovs.RxBytes != 12345 || ovs.TxBytes != 678) throw new Exception("management parse: " + ovs);
            if (OpenVpnEngine.ParseManagement("1761000000,AUTH,,,,,,\n", "").Up) throw new Exception("AUTH state counted as up");
            if (VpnFiles.SafeName("Office VPN (UK) — long name here") is not { Length: 15 } sn || sn.Contains(' ')) throw new Exception("safe name: " + VpnFiles.SafeName("Office VPN (UK) — long name here"));
            ok("VPN configs: WireGuard import splits the private and pre-shared keys to the vault side, drops PostUp (never runs commands), reads AllowedIPs as routes; render joins the keys back with the app's route choice (specific routes / everything / the tunnel's network only). OpenVPN import inlines-only, strips the <key>, refuses up/plugin/script-security and file references, reads route / redirect-gateway / DNS; render adds route-nopull + routes and the management channel. Routes parse, masks, coverage, the Windows routing table read back; wg show and OpenVPN management output parsed");
        }
        catch (Exception ex) { fail("VPN configs", ex); }

        // ---- the manager with a fake engine: keys to the vault, up before an SSH connection, down, idle rule ---------------------
        var credsWas = AppData.Current.Credentials.Count; var profilesWas = AppData.Current.VpnProfiles.ToList();
        var realEngine = VpnManager.Engines["wireguard"]; var hookWas = Ssh.SshConnector.BeforeConnect;
        try
        {
            var fake = new FakeEngine();
            VpnManager.Engines["wireguard"] = fake;
            var p = VpnManager.Import($"[Interface]\nPrivateKey = {WgKey}\nAddress = 10.66.0.2/24\n[Peer]\nPublicKey = {WgPub}\nAllowedIPs = 192.168.50.0/24\nEndpoint = 192.0.2.77:51820\n", "selftest-vpn.conf");
            p.Name = "selftest-vpn";
            if (AppData.Current.Credentials.Count != credsWas + 1 || AppData.Current.FindCredential(p.KeyCredentialId)?.Secret != WgKey || p.Config.Contains(WgKey)) throw new Exception("import didn't move the key to the vault");
            AppData.Current.VpnProfiles.Add(p); AppData.Save();
            var rendered = VpnManager.Render(p);
            if (!rendered.Contains(WgKey) || !rendered.Contains("AllowedIPs = 192.168.50.0/24")) throw new Exception("render");
            Ssh.SshConnector.BeforeConnect = VpnManager.EnsureForHostAsync;
            var host = new HostEntry { Name = "selftest-via-vpn", Address = "192.168.50.10", VpnProfileId = p.Id };
            await Ssh.SshConnector.BeforeConnect(host, CancellationToken.None);
            if (fake.Ups != 1 || !VpnManager.IsUp(p) || p.LastState != "up" || !fake.LastConfig.Contains(WgKey)) throw new Exception("the connect hook didn't bring the tunnel up");
            await Ssh.SshConnector.BeforeConnect(host, CancellationToken.None);
            if (fake.Ups != 1) throw new Exception("a tunnel already up was started again");
            await Ssh.SshConnector.BeforeConnect(new HostEntry { Name = "plain", Address = "192.0.2.1" }, CancellationToken.None);
            if (fake.Ups != 1) throw new Exception("a host without a VPN touched the tunnel");
            var st = await VpnManager.StatusAsync(p);
            if (!st.Up) throw new Exception("status");
            await VpnManager.DownAsync(p);
            if (VpnManager.IsUp(p) || fake.Downs != 1 || p.LastState != "down") throw new Exception("down");
            fake.Fail = true;
            try { await VpnManager.UpAsync(p); throw new Exception("a failing engine reported up"); } catch (InvalidOperationException) { }
            if (VpnManager.IsUp(p) || !p.LastState.StartsWith("✕")) throw new Exception("failure state");
            ok("VPN manager: import puts the key in the vault and keeps the configuration without it; render joins them in memory; the SSH connect hook brings a host's tunnel up once (fake engine) and leaves hosts without a VPN alone; status, down and a failed start are reported and audited");
        }
        catch (Exception ex) { fail("VPN manager", ex); }
        finally
        {
            VpnManager.Engines["wireguard"] = realEngine; Ssh.SshConnector.BeforeConnect = hookWas;
            AppData.Current.Credentials.RemoveAll(c => c.Name.StartsWith("VPN key: selftest-vpn"));
            AppData.Current.VpnProfiles = profilesWas; AppData.Save();
        }
    }

    private sealed class FakeEngine : IVpnEngine
    {
        public string Kind => "wireguard";
        public int Ups, Downs; public bool Fail; public string LastConfig = "";
        public string? Find(out string hint) { hint = ""; return "fake"; }
        public Task UpAsync(VpnProfile p, string renderedConfig, Func<string, string?> secret, CancellationToken ct) { if (Fail) throw new InvalidOperationException("fake failure"); Ups++; LastConfig = renderedConfig; return Task.CompletedTask; }
        public Task DownAsync(VpnProfile p, CancellationToken ct) { Downs++; return Task.CompletedTask; }
        public Task<VpnStatus> StatusAsync(VpnProfile p, CancellationToken ct) => Task.FromResult(new VpnStatus(Ups > Downs, Ups > Downs ? "up" : "down", "192.0.2.77:51820", DateTime.Now, 1, 2, ""));
        public string InterfaceName(VpnProfile p) => "fake";
    }
}
