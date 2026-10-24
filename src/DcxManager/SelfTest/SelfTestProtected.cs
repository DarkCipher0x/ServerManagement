using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.1 (ADR-039): admin-protected settings and Windows Hello. UAC and the elevated window are replaced by a
/// fake that does what the elevated copy does (read the request, check it, write the record) against a scratch store; Hello
/// by a fake with deterministic signatures. No window, prompt or dialog appears, and the real HKLM record is never read.
/// </summary>
public static class ProtectedSettingsTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;
        var storeWas = ProtectedSettings.Store;
        var dir = Path.Combine(SecureStore.DataDir, "protected-unit");
        var store = new ProtectedSettings.FileStore(dir);
        var saved = (s.TelnetEnabled, s.OfflineMode, s.LocalApiEnabled, s.AiRedactSecrets, s.HelloConfirm, s.HelloUnlock, s.AutoLockMinutes);
        int elevations = 0;
        string? lastPlan = null;
        int answer = 0;
        // What the elevated copy does, minus UAC and its window: read, check, and write when "approved".
        int FakeElevate(string path)
        {
            elevations++;
            try
            {
                var plan = ProtectedSettings.ReadRequest(path);
                lastPlan = string.Join("; ", plan.Lines.Select(l => $"{l.Key}: {l.From} → {l.To}"));
                if (answer != 0) return answer;
                ProtectedSettings.Commit(plan, "SELFTEST\\admin");
                return 0;
            }
            catch (InvalidDataException) { return 2; }
        }
        try
        {
            ProtectedSettings.UseStoreForTest(store);
            AdminGate.FakeElevate = FakeElevate;

            // 1. The trust rule on made-up permissions.
            try
            {
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var me = WindowsIdentity.GetCurrent().User!;
                if (ProtectedSettings.AclProblem(admins, new[] { (admins, RegistryRights.FullControl), (system, RegistryRights.FullControl), (users, RegistryRights.ReadKey) }) != null) throw new Exception("the intended ACL was rejected");
                if (ProtectedSettings.AclProblem(admins, new[] { (users, RegistryRights.SetValue) }) == null) throw new Exception("Users with SetValue accepted");
                if (ProtectedSettings.AclProblem(admins, new[] { (me, RegistryRights.FullControl) }) == null) throw new Exception("write access for the user accepted");
                if (ProtectedSettings.AclProblem(me, Array.Empty<(SecurityIdentifier, RegistryRights)>()) == null) throw new Exception("a user-owned key accepted");
                if (ProtectedSettings.AclProblem(admins, new[] { (new SecurityIdentifier(WellKnownSidType.WorldSid, null), RegistryRights.WriteKey) }) == null) throw new Exception("Everyone with WriteKey accepted");
                ok("protected settings: the store is trusted only when its owner and everyone who can write it are administrators / SYSTEM / TrustedInstaller (Users, Everyone or a user owner are refused); readers may be anyone");
            }
            catch (Exception ex) { fail("protected settings ACL rule", ex); }

            // 2. Requests: the first carries everything; the elevated side refuses anything odd.
            try
            {
                var req = ProtectedSettings.BuildRequest(s, new Dictionary<string, JsonNode?> { ["net.telnet"] = true }, "test");
                if (req.Values.Count != ProtectedSettings.Items.Count || req.Values["net.telnet"]?.GetValue<bool>() != true) throw new Exception("the first request doesn't carry every protected setting");
                string Write(object o) { var p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"); Directory.CreateDirectory(dir); File.WriteAllText(p, o as string ?? JsonSerializer.Serialize(o, SecureStore.Json)); return p; }
                Exception? Refused(string path, Func<DateTime>? now = null) { try { ProtectedSettings.ReadRequest(path, now); return null; } catch (Exception ex) { return ex; } }
                var good = Write(req);
                if (Refused(good) != null) throw new Exception("a good request was refused: " + Refused(good)!.Message);
                if (Refused(good, () => DateTime.UtcNow.AddMinutes(11)) is not InvalidDataException) throw new Exception("an expired request was accepted");
                if (Refused(Write("not json")) is not InvalidDataException) throw new Exception("garbage accepted");
                if (Refused(Write(new string(' ', ProtectedSettings.MaxRequestBytes + 10))) is not InvalidDataException) throw new Exception("an oversized request accepted");
                var unknown = JsonNode.Parse(File.ReadAllText(good))!.AsObject(); unknown["Values"]!.AsObject()["net.firewallOff"] = true;
                if (Refused(Write(unknown.ToJsonString())) is not InvalidDataException) throw new Exception("an unknown setting accepted");
                var badValue = JsonNode.Parse(File.ReadAllText(good))!.AsObject(); badValue["Values"]!["net.localApiPort"] = 80;
                if (Refused(Write(badValue.ToJsonString())) is not InvalidDataException) throw new Exception("a port below 1024 accepted");
                var badSid = JsonNode.Parse(File.ReadAllText(good))!.AsObject(); badSid["Sid"] = "S-1-5-21-nope";
                if (Refused(Write(badSid.ToJsonString())) is not InvalidDataException) throw new Exception("a bad SID accepted");
                var wrongFormat = JsonNode.Parse(File.ReadAllText(good))!.AsObject(); wrongFormat["Format"] = "dcx-appearance";
                if (Refused(Write(wrongFormat.ToJsonString())) is not InvalidDataException) throw new Exception("another format accepted");
                foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("S-", StringComparison.Ordinal))) File.Delete(f);
                ok($"protected settings requests: the first carries all {ProtectedSettings.Items.Count} protected settings; the elevated side refuses expired, garbage, oversized, unknown-setting, out-of-range, bad-user and other-format requests");
            }
            catch (Exception ex) { fail("protected settings requests", ex); }

            // 3. The gate end to end (UAC faked): protect, change, refuse, tighten, enforce over an edited settings file.
            try
            {
                if (ProtectedSettings.Active) throw new Exception("the scratch store already holds a record");
                s.TelnetEnabled = false; s.OfflineMode = false;
                if (!await ProtectedSettings.ChangeAsync("allow Telnet (test)", "net.telnet", true)) throw new Exception("an approved change failed");
                if (!ProtectedSettings.Active || !ProtectedSettings.IsProtected("lock.sandbox") || !s.TelnetEnabled || elevations != 1) throw new Exception($"after approval: active {ProtectedSettings.Active}, telnet {s.TelnetEnabled}, elevations {elevations}");
                if (lastPlan?.Contains("net.telnet: not protected → on") != true) throw new Exception("the admin saw: " + lastPlan);
                // The user's own settings file edited behind the app's back loses to the record.
                s.TelnetEnabled = false; AppSettings.Save();
                ProtectedSettings.Reload();
                if (!ProtectedSettings.ApplyTo(s).Contains("net.telnet") || !s.TelnetEnabled) throw new Exception("an edited settings file won over the protected value");
                // Declined at UAC: nothing changes.
                answer = 1;
                if (await ProtectedSettings.ChangeAsync("enable the local API (test)", "net.localApi", true) || s.LocalApiEnabled) throw new Exception("a declined change applied");
                // A stricter value applies for the session even when declined, and comes back after a "restart".
                if (await ProtectedSettings.ChangeAsync("offline on (test)", "net.offline", true) || !s.OfflineMode) throw new Exception("offline on didn't apply for the session");
                ProtectedSettings.Reload(); ProtectedSettings.ApplyTo(s);
                if (s.OfflineMode) throw new Exception("a declined stricter value survived a restart");
                Offline.Reapply();
                // A refused (invalid) request changes nothing.
                answer = 0;
                int before = elevations;
                if (!await ProtectedSettings.ChangeAsync("offline on (test)", "net.offline", true) || !s.OfflineMode || elevations != before + 1) throw new Exception("an approved stricter change failed");
                if (!await ProtectedSettings.ChangeAsync("offline off (test)", "net.offline", false) || s.OfflineMode) throw new Exception("offline off failed");
                // No real change: no prompt.
                before = elevations;
                await ProtectedSettings.ChangeAsync("nothing (test)", "net.telnet", true);
                if (elevations != before) throw new Exception("an unchanged value asked for approval");
                // History is kept and short.
                if (ProtectedSettings.Current!.History.Count < 3 || ProtectedSettings.Current.History[0].By != "SELFTEST\\admin") throw new Exception("history");
                ok($"protected settings gate: the first change protects every setting (the admin window lists “not protected → on”), an edited settings file loses to the record at start, a change declined at UAC applies nothing, a stricter value (offline on) applies at once but is gone after a restart unless approved, unchanged values don't prompt, changes are kept in a short history ({elevations} fake elevations)");
            }
            catch (Exception ex) { fail("protected settings gate", ex); }

            // 4. A store others can write is ignored (and says why).
            try
            {
                store.LooseAcl = "BUILTIN\\Users can change it";
                ProtectedSettings.Reload();
                if (ProtectedSettings.Active || ProtectedSettings.Problem?.Contains("Users") != true) throw new Exception("a loose ACL was trusted");
                store.LooseAcl = null;
                ProtectedSettings.Reload();
                if (!ProtectedSettings.Active) throw new Exception("the record didn't come back");
                // A record with a bad value: that value is dropped and reported, the rest stands.
                var rec = JsonNode.Parse(store.Read(ProtectedSettings.CurrentSid)!)!.AsObject();
                rec["Values"]!["lock.autoLockMinutes"] = 99999;
                store.Write(ProtectedSettings.CurrentSid, rec.ToJsonString());
                ProtectedSettings.Reload();
                if (ProtectedSettings.IsProtected("lock.autoLockMinutes") || !ProtectedSettings.IsProtected("net.telnet") || ProtectedSettings.Problem?.Contains("lock.autoLockMinutes") != true) throw new Exception("a bad value in the record");
                ok("protected settings trust: a record others could write is ignored and reported (Settings shows it); an invalid value in a record is dropped and named, the rest still applies");
            }
            catch (Exception ex) { fail("protected settings trust", ex); }

            // 5. The Settings card and the switches.
            try
            {
                if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
                main.Navigate("settings");
                await Settle(400);
                var sv = SettingsView.Current ?? throw new Exception("no settings view");
                sv.RevealCategory("Security & privacy");
                await Settle(300);
                if (sv.VisibleCardsForTest.FirstOrDefault() != "Admin protection") throw new Exception("Admin protection isn't the first card: " + string.Join(", ", sv.VisibleCardsForTest));
                var card = sv.CardForTest("Admin protection")!;
                var text = string.Join(" ", Menus.Descendants<TextBlock>(card).Select(t => t.Text));
                if (!text.Contains("Protected") || !text.Contains("Telnet")) throw new Exception("card: " + text[..Math.Min(200, text.Length)]);
                var hello = Menus.Descendants<CheckBox>(sv.CardForTest("Security & encryption")!).Where(c => (c.Content as TextBlock)?.Text.Contains("Windows Hello") == true).ToList();
                if (hello.Count != 2 || hello.Any(c => c.ToolTip is not string tip || !tip.Contains("admin approval") || !((TextBlock)c.Content).Text.StartsWith("🛡", StringComparison.Ordinal))) throw new Exception("Hello switches");
                snap(main, Path.Combine(reportDir, "shots", "settings-admin-protection.png"));
                ok("protected settings in Settings: Admin protection is the first card of Security & privacy (state, what is protected, recent changes), the Windows Hello switches say they need administrator approval; rendered settings-admin-protection.png");
            }
            catch (Exception ex) { fail("protected settings card", ex); }

            // 6. Windows Hello (faked): sign-in wraps the key, the right signature opens it, a wrong one doesn't; the AI lock.
            try
            {
                var fake = new WindowsHello.FakeHello();
                WindowsHello.Fake = fake;
                SecureStore.SetPassword("correct horse battery staple", requireOnStart: false);
                var (name, challenge) = SecureStore.NewHelloSetup();
                var sig = await WindowsHello.CreateAndSignAsync(name, challenge) ?? throw new Exception("fake Hello didn't sign");
                SecureStore.SetHello(name, challenge, sig);
                if (!SecureStore.HasHello || !SecureStore.PasswordRequiredOnStart) throw new Exception("Hello on but the Windows-account copy is still there");
                var again = await WindowsHello.SignAsync(SecureStore.HelloKeyName!, SecureStore.HelloChallenge!) ?? throw new Exception("no signature");
                if (!SecureStore.VerifyHello(again) || SecureStore.VerifyHello(System.Security.Cryptography.RandomNumberGenerator.GetBytes(again.Length))) throw new Exception("signature check");
                // 0.6.12 (ADR-090): the Hello wrap is post-quantum (the signature AND a DPAPI-protected secret of this Windows
                // account); still one Hello prompt and nothing else to do. A pre-0.6.12 wrap still opens and is upgraded then.
                if (!SecureStore.HelloPostQuantum) throw new Exception("a new Hello wrap isn't the post-quantum hybrid");
                int helloPrompts = fake.Prompts;
                var once = await WindowsHello.SignAsync(SecureStore.HelloKeyName!, SecureStore.HelloChallenge!) ?? throw new Exception("no signature");
                if (!SecureStore.UnlockWithHello(once) || fake.Prompts != helloPrompts + 1) throw new Exception("unlocking with Hello took more than the one Hello prompt");
                SecureStore.SetHelloLegacyForTest(name, challenge, sig);
                if (SecureStore.HelloPostQuantum || !SecureStore.VerifyHello(again)) throw new Exception("a pre-0.6.12 Hello wrap");
                if (!SecureStore.UnlockWithHello(again) || !SecureStore.HelloPostQuantum || !SecureStore.VerifyHello(again)) throw new Exception("a pre-0.6.12 Hello wrap wasn't upgraded on unlock");
                try { SecureStore.RemovePassword(); throw new Exception("the password (Hello's fallback) could be removed"); } catch (InvalidOperationException) { }
                fake.Answer = WindowsHello.Outcome.Cancelled;
                if (await WindowsHello.SignAsync(name, challenge) != null) throw new Exception("a cancelled Hello signed");
                fake.Answer = WindowsHello.Outcome.Verified;
                SecureStore.RemoveHello();
                if (SecureStore.HasHello || SecureStore.PasswordRequiredOnStart) throw new Exception("Hello off didn't bring the Windows-account copy back");
                SecureStore.RemovePassword();
                // The AI features lock with Hello: no password, Hello confirms each gated change.
                FeatureLock.ResetForTest();
                FeatureLock.SetHello();
                if (!FeatureLock.IsSet || !FeatureLock.IsHello) throw new Exception("Hello lock not set");
                if (FeatureLock.Gate("test", () => "a typed password") != null) throw new Exception("a verified Hello was refused");
                fake.Answer = WindowsHello.Outcome.Cancelled;
                if (FeatureLock.Gate("test", () => "a typed password") == null) throw new Exception("a cancelled Hello passed the lock (or a typed password did)");
                if (FeatureLock.RemoveHello() == FeatureLock.Result.Ok) throw new Exception("a cancelled Hello removed the lock");
                fake.Answer = WindowsHello.Outcome.Verified;
                if (FeatureLock.RemoveHello() != FeatureLock.Result.Ok || FeatureLock.IsSet) throw new Exception("removing the Hello lock");
                // Confirm sensitive actions: Hello asked, a cancel stops the action.
                s.HelloConfirm = true;
                int prompts = fake.Prompts;
                if (!SensitiveAction.Confirm("test") || fake.Prompts != prompts + 1) throw new Exception("confirm with Hello");
                fake.Answer = WindowsHello.Outcome.Cancelled;
                if (SensitiveAction.Confirm("test")) throw new Exception("a cancelled confirm let the action go ahead");
                fake.Answer = WindowsHello.Outcome.Verified;
                ok($"Windows Hello (faked): sign-in seals the data key under a key derived from the Hello signature (0.6.12: post-quantum hybrid with a DPAPI-protected secret, still one Hello prompt; a pre-0.6.12 wrap opens and is upgraded on unlock) and drops the Windows-account copy, only the right signature opens it, a cancelled Hello signs nothing, the master password can't be removed while it is Hello's fallback, Hello off restores the previous protection; the AI features lock works with Hello instead of a password; sensitive actions stop when Hello is cancelled ({fake.Prompts} fake prompts)");
            }
            catch (Exception ex) { fail("Windows Hello", ex); }
            finally
            {
                if (SecureStore.HasHello) try { SecureStore.RemoveHello(); } catch { }
                if (SecureStore.HasPassword) try { SecureStore.RemovePassword(); } catch { }
                FeatureLock.ResetForTest();
                WindowsHello.Fake = null;
            }
        }
        finally
        {
            AdminGate.FakeElevate = null;
            ProtectedSettings.UseStoreForTest(storeWas);
            (s.TelnetEnabled, s.OfflineMode, s.LocalApiEnabled, s.AiRedactSecrets, s.HelloConfirm, s.HelloUnlock, s.AutoLockMinutes) = saved;
            AppSettings.Save();
            Offline.Reapply();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
