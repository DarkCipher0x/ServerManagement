namespace DcxManager.Services;

/// <summary>What a network scan found on one address, for identification: names and banners only, never a secret.</summary>
public sealed record HostClue(string Address, string Name, IReadOnlyList<HostCluePort> Ports);
public sealed record HostCluePort(int Port, string Service, string Detail);

/// <summary>A suggestion for one address: a short name, the platform (linux, windows, macos, network, other), a folder,
/// what the machine probably is, and why. Everything is shown in the review grid and can be changed before importing.</summary>
public sealed record HostSuggestion(string Address, string Name, string Platform, string Folder, string Role, string Reason);

/// <summary>
/// Identifies discovered machines (0.5.1 #18). The AI module registers the only implementation; with AI off there is
/// none, and the host import uses its own rules (banners, ports) without the "✦ Identify with AI" step.
/// </summary>
public interface IHostIdentifier
{
    /// <summary>Null when identification may run now, else why not.</summary>
    string? WhyNot();
    Task<IReadOnlyList<HostSuggestion>> IdentifyAsync(IReadOnlyList<HostClue> clues, CancellationToken ct);
}

public static class HostIdentify
{
    public static IHostIdentifier? Current { get; private set; }
    public static void Register(IHostIdentifier identifier) => Current ??= identifier;
    internal static void ResetForTest() => Current = null;
}

/// <summary>The one-click Foundry Local set-up for screens outside the AI module (Quick Start, 0.5.1 #14): registered by the
/// AI module; null when AI is off. Returns the outcome line ("✓ ready …" or why not).</summary>
public static class LocalAiSetup
{
    public static Func<Task<string>>? FoundryOneClick { get; private set; }
    public static void Register(Func<Task<string>> run) => FoundryOneClick ??= run;
    internal static void ResetForTest() => FoundryOneClick = null;
}
