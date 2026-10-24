using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.SignIn;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// Start-up steps added in 0.3.0 (§19, §23), kept out of App.xaml.cs: choosing the profile before any store opens, the
/// directory sign-in gate for a bound profile, and the first-run setup after the main window shows. Never in the
/// self-test (no window, no dialog): there the scratch store is the only profile.
/// </summary>
public static class StartupFlow
{
    /// <summary>Picks the profile (command line, the only one, the remembered one, or the picker). False = quit.</summary>
    public static bool ChooseProfile(string[] args)
    {
        var ix = Profiles.LoadWithDefault();
        ProfileInfo? p = null;
        if (Profiles.Requested(args) is { } id) p = ix.Profiles.FirstOrDefault(x => x.Id == id);
        bool directoryOnly = Profiles.DirectoryRequiredByPolicy();
        if (p == null && !directoryOnly && ix.Profiles.Count == 1) p = ix.Profiles[0];
        // 0.5.0 (ADR-062): signed in to Windows with the account a profile is bound to → that profile, no picker; the
        // directory sign-in then uses the Windows session (nothing typed) and Windows Hello opens the store when set up.
        if (p == null) p = Profiles.ForWindowsAccount(ix);
        if (p == null && ix.OpenLastWithoutAsking && ix.LastUsed != null) p = ix.Profiles.FirstOrDefault(x => x.Id == ix.LastUsed && (!directoryOnly || x.Kind == "directory"));
        if (p == null)
        {
            var w = new ProfilePickerWindow(ix);
            if (w.ShowDialog() != true || w.Chosen == null) return false;
            p = w.Chosen;
        }
        if (p.Kind == "local" && directoryOnly) { Dialogs.Error(App.ProductName, "Local profiles are not allowed on this PC (Group Policy): choose a profile that signs in with your organisation.", explain: false); return false; }
        Profiles.Select(p);
        return true;
    }

    /// <summary>After the store is open: a directory-bound profile must sign in (or have a recent proof). False = quit.</summary>
    public static bool DirectoryGate()
    {
        if (Profiles.Current is not { Kind: "directory", Directory: { } b }) return true;
        string? why = Wait(DirectorySignIn.OpenAsync(b, q => Dialogs.Prompt("Sign in", q, password: true)));
        if (why == null) return true;
        Dialogs.Error(App.ProductName, $"This profile signs in with your organisation ({b.Describe()}), and the sign-in didn't succeed:\n\n{why}\n\nThe profile stays closed.", explain: false);
        return false;
    }

    /// <summary>Runs the task while the UI thread keeps pumping (the Entra flow needs the browser and the loopback listener).</summary>
    private static T Wait<T>(Task<T> t)
    {
        if (!t.IsCompleted)
        {
            var frame = new DispatcherFrame();
            t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return t.GetAwaiter().GetResult();
    }

    /// <summary>A new store: the guided setup, every step optional (0.3.0 §19). Re-run from Settings → About.</summary>
    public static void MaybeRunSetup(Window owner)
    {
        if (AppSettings.Current.SetupDone || AppSettings.ExistedBeforeLoad) return;
        try { SetupWindow.Run(owner); }
        catch (Exception ex) { App.Log("First-run setup", ex); }
        AppSettings.Current.SetupDone = true;
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("First-run setup", ex); }
    }

    /// <summary>Restarts the app into another profile (the running one closes without the exit prompt).</summary>
    public static void SwitchTo(ProfileInfo p)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        if (MainWindow.Instance is { } mw)
        {
            mw.ExitWithoutPrompt = true;
            mw.Closed += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { ArgumentList = { "--profile", p.Id }, UseShellExecute = false });
            mw.Close();
        }
    }
}
