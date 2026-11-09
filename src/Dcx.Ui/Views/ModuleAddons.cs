using System.Windows;

namespace DcxManager.Views;

/// <summary>
/// The contract between the app and a module add-on (ADR-120). A module add-on is an assembly in a signed add-on package:
/// the app loads it only when its module.json verifies with DarkCipher Systems' release keys, creates the type the
/// manifest names (an <see cref="IAppModule"/>) and registers it like a built-in module. Everything the module does is
/// in the add-on: its screens, services, settings (<c>ModuleSettings.Attach</c>), policies (<c>PolicySettings.RegisterModule</c>),
/// help (<see cref="IAddonPackage.ReadText"/> + <c>HelpKb.AddModuleHelp</c>) and its self-test. Removing the add-on removes
/// all of it; the app names none of it. <see cref="CurrentApi"/> rises when this contract changes incompatibly; a module
/// made for another API isn't installed or loaded.
/// </summary>
public static class ModuleAddonApi
{
    /// <summary>Kept equal to <c>AddonModuleInfo.CurrentApi</c> (checked by the self-test).</summary>
    public const int CurrentApi = 1;
}

/// <summary>The installed package a module came from: its files (decrypted and checked against the signed hashes on every
/// read) and a data folder of its own.</summary>
public interface IAddonPackage
{
    string Id { get; }
    string Version { get; }
    /// <summary>A package file's bytes; throws <see cref="System.IO.InvalidDataException"/> when it was changed.</summary>
    byte[] Read(string relativePath);
    string ReadText(string relativePath);
    /// <summary>The add-on's own data folder (kept across updates, removed with the add-on).</summary>
    string DataFolder { get; }
}

/// <summary>A module add-on that wants its package (to read its own files) before it registers.</summary>
public interface IAddonModule : IAppModule
{
    void Attach(IAddonPackage package);
}

/// <summary>A module's own self-test: the app's self-test runs it (with the module loaded) and reports its lines.</summary>
public interface IModuleSelfTest
{
    Task RunAsync(Window main, Action<string> ok, Action<string, Exception?> fail);
}
