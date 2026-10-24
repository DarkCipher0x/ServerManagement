using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// Spec §55 / ADR-017: environment labels and production safeguards. Confirmations are answered by
/// <see cref="Dialogs.ConfirmForTests"/> (recorded, never shown); terminals are created without connecting.
/// </summary>
public static class EnvTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var prod = new HostEntry { Name = "env-prod-web", Address = "192.0.2.21", Username = "ops", Environment = "prod" };
        var lab = new HostEntry { Name = "env-lab-box", Address = "192.0.2.22", Username = "ops", Environment = "LAB" };
        var none = new HostEntry { Name = "env-unlabelled", Address = "192.0.2.23", Username = "ops" };
        bool confirmWas = AppSettings.Current.ConfirmDestructive, pasteWas = AppSettings.Current.TerminalConfirmPaste;
        var tabs = new List<TabItem>();
        try
        {
            // 1. Vocabulary, colours, persistence.
            try
            {
                var cases = new (string In, string Out)[] { ("prod", "PRODUCTION"), (" Live ", "PRODUCTION"), ("dev", "DEVELOPMENT"), ("uat", "TEST"), ("Staging", "TEST"), ("lab", "LAB"), ("local", "LOCAL"), ("", ""), ("(not set)", ""), ("dr-site", "DR-SITE") };
                foreach (var (i, o) in cases) if (Envs.Normalize(i) != o) throw new Exception($"normalize '{i}' → '{Envs.Normalize(i)}'");
                if (!Envs.IsProduction("PRD") || Envs.IsProduction("DR-SITE") || Envs.IsProduction("")) throw new Exception("IsProduction");
                if (Envs.Short("PRODUCTION") != "PROD" || Envs.Short("DEVELOPMENT") != "DEV" || Envs.Short("LAB") != "LAB") throw new Exception("short labels");
                foreach (var e in Envs.All.Append("DR-SITE"))
                    if (Application.Current.TryFindResource(Envs.BrushKey(e)) is not System.Windows.Media.SolidColorBrush) throw new Exception("no theme colour for " + e);
                if (Envs.BrushKey("PRODUCTION") == Envs.BrushKey("LAB") || Envs.BrushKey("DR-SITE") != "EnvOther") throw new Exception("brush keys");
                if (Infra.Infra.Environments != Envs.All) throw new Exception("infrastructure uses another list");

                AppData.Current.Hosts.AddRange(new[] { prod, lab, none });
                prod.Environment = Envs.Normalize(prod.Environment);
                AppData.Save();
                if (Envs.For(prod) != "PRODUCTION" || Envs.For(none) != "" || Envs.For(null) != "LOCAL") throw new Exception("Envs.For");
                var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx")));
                if (raw.Contains("env-prod-web") || raw.Contains("PRODUCTION")) throw new Exception("plaintext host data");
                // An edit replaces the host object; anything holding the old one must still see the new environment.
                var edited = prod.Clone(); edited.Environment = "TEST";
                int idx = AppData.Current.Hosts.FindIndex(h => h.Id == prod.Id); AppData.Current.Hosts[idx] = edited;
                if (Envs.For(prod) != "TEST") throw new Exception("stale host object not resolved by id");
                AppData.Current.Hosts[idx] = prod;
                var row = new HostRow(prod);
                if (row.EnvText != "PRODUCTION" || row.EnvBrush == null || new HostRow(none).EnvBrush != null) throw new Exception("host list cell");
                var editor = new HostEditWindow(prod);
                if (editor.EnvBox.SelectedItem as string != "PRODUCTION" || !((IEnumerable<string>)editor.EnvBox.ItemsSource).SequenceEqual(new[] { "(not set)" }.Concat(Envs.All))) throw new Exception("host editor list");
                editor.Close();
                ok("environments: one vocabulary (aliases folded, custom labels kept, never production), theme colour for each + text always printed, host field encrypted, stale host objects resolved by id, host list + editor, infrastructure shares the list");
            }
            catch (Exception ex) { fail("environment vocabulary", ex); }

            // 2. Production safeguards.
            try
            {
                Dialogs.ConfirmForTests = true;
                AppSettings.Current.ConfirmDestructive = false;   // PRODUCTION must still ask
                Dialogs.SelfTestConfirms.Clear();
                if (!Envs.Confirm(prod, "Reboot", "Reboot it?", "Reboot", danger: false)) throw new Exception("answer");
                var c = Dialogs.SelfTestConfirms.Single();
                if (!c.Danger || c.TypeToConfirm != prod.Display || c.Env != "PRODUCTION") throw new Exception($"production confirm: danger={c.Danger} type={c.TypeToConfirm} env={c.Env}");
                Dialogs.SelfTestConfirms.Clear();
                Envs.Confirm(prod, "Delete", "Delete 3 folders?", "Delete", typeToConfirm: "delete");
                if (Dialogs.SelfTestConfirms.Single().TypeToConfirm != prod.Display) throw new Exception("PRODUCTION must always ask for the host's name");
                Dialogs.SelfTestConfirms.Clear();
                AppSettings.Current.ConfirmDestructive = true;
                Envs.Confirm(lab, "Reboot", "Reboot it?", "Reboot");
                if (Dialogs.SelfTestConfirms.Single() is not { Env: "LAB", TypeToConfirm: null }) throw new Exception("lab confirm");
                Dialogs.SelfTestConfirms.Clear();
                Envs.Confirm(none, "Reboot", "Reboot it?", "Reboot");
                if (Dialogs.SelfTestConfirms.Single().Env != null) throw new Exception("unlabelled host got a banner");
                Dialogs.SelfTestConfirms.Clear();
                Envs.ConfirmMany(new[] { lab, prod, none }, "Run on hosts", "Run uptime");
                var many = Dialogs.SelfTestConfirms.Single();
                if (many.TypeToConfirm != "PRODUCTION" || !many.Danger || !many.Message.Contains("PRODUCTION: env-prod-web") || !many.Message.Contains("LAB: env-lab-box") || !many.Message.Contains("(no environment): env-unlabelled"))
                    throw new Exception("multi-host confirm: " + many.Message);
                if (many.Message.IndexOf("PRODUCTION:", StringComparison.Ordinal) > many.Message.IndexOf("LAB:", StringComparison.Ordinal)) throw new Exception("production not listed first");
                Dialogs.SelfTestConfirms.Clear();
                Envs.ConfirmMany(new[] { lab, none }, "Run on hosts", "Run uptime");
                if (Dialogs.SelfTestConfirms.Single().TypeToConfirm != null) throw new Exception("no production, yet typing required");

                AppSettings.Current.TerminalConfirmPaste = false;
                var multi = PasteGuard.Check("systemctl stop nginx\nrm -rf /var/cache/x\n");
                if (!TerminalView.PasteNeedsConfirm(multi, "PRODUCTION")) throw new Exception("multi-line paste into PRODUCTION not shown first");
                if (TerminalView.PasteNeedsConfirm(multi, "LAB")) throw new Exception("paste setting ignored outside production");
                if (TerminalView.PasteNeedsConfirm(PasteGuard.Check("uptime"), "PRODUCTION")) throw new Exception("single safe line asked");
                AppSettings.Current.TerminalConfirmPaste = pasteWas;

                // Bulk change: taking PRODUCTION away asks; refusing keeps it.
                Dialogs.SelfTestConfirms.Clear();
                Dialogs.ConfirmForTests = false;
                if (HostsView.SetEnvironment(new[] { prod, lab }, "TEST") || Envs.For(prod) != "PRODUCTION") throw new Exception("removing PRODUCTION without consent");
                if (!Dialogs.SelfTestConfirms.Any(x => x.Title == "Remove PRODUCTION label")) throw new Exception("no question before removing PRODUCTION");
                Dialogs.ConfirmForTests = true;
                if (!HostsView.SetEnvironment(new[] { lab, none }, "DEVELOPMENT") || Envs.For(lab) != "DEVELOPMENT" || Envs.For(none) != "DEVELOPMENT") throw new Exception("bulk set");
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                if (!audit.Any(a => a.Target == "env-lab-box" && a.Action == "environment" && a.Result == "LAB → DEVELOPMENT")) throw new Exception("environment change not audited");
                HostsView.SetEnvironment(new[] { lab }, "LAB"); HostsView.SetEnvironment(new[] { none }, "");
                ok("production safeguards: PRODUCTION always a danger confirmation with the host's name typed (even with confirmations off, overriding other typed words), banner for labelled hosts only, multi-host runs grouped by environment with PRODUCTION typed, multi-line paste into PRODUCTION always previewed, removing PRODUCTION asks, bulk changes audited");
            }
            catch (Exception ex) { fail("production safeguards", ex); }
            finally { Dialogs.ConfirmForTests = null; Dialogs.SelfTestConfirms.Clear(); AppSettings.Current.ConfirmDestructive = confirmWas; AppSettings.Current.TerminalConfirmPaste = pasteWas; }

            // 3. Tabs and views (terminals created without connecting).
            try
            {
                int subs = Envs.ChangedSubscribers;
                var prodTerm = new TerminalView(prod, connectOnOpen: false);
                var local = LocalShells.All.FirstOrDefault(p => !p.Wsl) ?? throw new Exception("no local shell profile");
                var localTerm = new TerminalView(new LocalTarget(local), connectOnOpen: false);
                var labTerm = new TerminalView(lab, connectOnOpen: false);
                tabs.Add(main.AddTab("⌨ " + prod.Display, prodTerm));
                tabs.Add(main.AddTab("❯ " + local.Name, localTerm));
                tabs.Add(main.AddTab("⌨ " + lab.Display, labTerm));
                main.Panes.Select(tabs[0]);
                await Settle(500);
                if (EnvTab.GetLabel(tabs[0]) != "PROD" || !EnvTab.GetShown(tabs[0]) || EnvTab.GetBrush(tabs[0]) == null) throw new Exception("production tab label");
                if (EnvTab.GetLabel(tabs[1]) != "LOCAL" || EnvTab.GetLabel(tabs[2]) != "LAB") throw new Exception($"tab labels: {EnvTab.GetLabel(tabs[1])}, {EnvTab.GetLabel(tabs[2])}");
                var strip = Menus.Descendants<Border>(prodTerm).FirstOrDefault(b => b.Child is TextBlock { Text: "PRODUCTION" });
                // Fully readable, not squeezed by the toolbar buttons (the first render showed an empty sliver).
                if (strip is not { Visibility: Visibility.Visible } || strip.ActualWidth < ((TextBlock)strip.Child).DesiredSize.Width + 8) throw new Exception("terminal strip: " + strip?.ActualWidth);
                var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
                snap(main, Path.Combine(shots, "environments.png"));

                // A host edit reaches open tabs and strips live.
                Dialogs.ConfirmForTests = true;
                HostsView.SetEnvironment(new[] { lab }, "TEST");
                await Settle(300);
                if (EnvTab.GetLabel(tabs[2]) != "TEST") throw new Exception("tab not updated: " + EnvTab.GetLabel(tabs[2]));
                if (!Menus.Descendants<Border>(labTerm).Any(b => b.Child is TextBlock { Text: var t2 } && t2 == "TEST")) throw new Exception("strip not updated");
                HostsView.SetEnvironment(new[] { lab }, "LAB");
                Dialogs.ConfirmForTests = null;

                foreach (var t in tabs) main.CloseTab(t);
                tabs.Clear();
                await Settle(300);
                if (Envs.ChangedSubscribers != subs) throw new Exception($"strips left handlers: {subs} → {Envs.ChangedSubscribers}");
                ok("environment tabs: PROD / LOCAL / LAB labels and coloured top edge on host tabs, a strip in the terminal, live update after a host edit, no handler left after closing; rendered environments.png");
            }
            catch (Exception ex) { fail("environment tabs", ex); }
        }
        finally
        {
            Dialogs.ConfirmForTests = null;
            foreach (var t in tabs) { try { main.CloseTab(t); } catch { } }
            AppData.Current.Hosts.RemoveAll(h => h.Id == prod.Id || h.Id == lab.Id || h.Id == none.Id);
            AppData.Save();
        }
    }
}
