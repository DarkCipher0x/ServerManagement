namespace DcxManager.Services;

/// <summary>A file a screen hands to a send target (a screenshot, a report, terminal output, a file from the commander).</summary>
public sealed record SendFile(string Name, byte[] Bytes, string? Mime = null);

/// <summary>What a screen sends: a subject (where it came from, shown to the user), text for the message, and files.</summary>
public sealed record SendItem(string Subject, string Text, IReadOnlyList<SendFile> Files)
{
    public static SendItem OfText(string subject, string text) => new(subject, text, Array.Empty<SendFile>());
    public static SendItem OfFile(string subject, SendFile file, string text = "") => new(subject, text, new[] { file });
}

/// <summary>
/// Where the screens' "💬 Send to a chat…" goes (ADR-117): one target at most, registered by a module (the chat). The
/// screens build the material; the target always shows it to the user before anything leaves the PC (the chat opens the
/// room with the text in the composer and the files attached, and nothing is sent until Send). No target registered =
/// no such item on any screen.
/// </summary>
public interface ISendTarget
{
    /// <summary>Null when sending may be offered now, else why not (the item is then disabled with this reason).</summary>
    string? WhyNot();
    /// <summary>The largest file the target takes (screens skip reading anything bigger).</summary>
    long MaxFileBytes { get; }
    /// <summary>Asks where it goes and shows it there for review; the user sends it.</summary>
    Task SendAsync(SendItem item);
}

public static class SendTo
{
    public static ISendTarget? Current { get; private set; }

    /// <summary>A module registers its target (one at most; the first stays).</summary>
    public static void Register(ISendTarget target) => Current ??= target;

    internal static void ResetForTest() => Current = null;
}
