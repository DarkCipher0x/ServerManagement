using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// What this PC allows in chats (ADR-117), from the settings that Group Policy can set (category CipherChat): files,
/// their size, calls, sharing from the app's screens and message text in notifications. Every screen asks here, so a
/// rule is one line to change.
/// </summary>
public static class ChatPolicy
{
    private static AppSettings S => AppSettings.Current;

    public static string? FilesBlocked() => S.ChatAllowFiles ? null : "Files and pictures in chats are turned off on this PC (your organisation's policy or Settings).";

    public static long MaxFileBytes => Math.Clamp(S.ChatMaxFileMB, 1, 100) * 1024L * 1024L;

    public static string? CallsBlocked() => S.ChatAllowCalls ? null : "Calls are turned off on this PC (your organisation's policy or Settings).";

    public static string? ShareBlocked() => S.ChatShareFromScreens ? null : "Sharing into chats from the app's screens is turned off on this PC (your organisation's policy or Settings).";

    /// <summary>The notification text for a new message: who and where, plus the text only when previews are allowed.</summary>
    public static string ToastText(string who, string where, string text) =>
        S.ChatToastPreviews ? who + " in " + where + ": " + (text.Length <= 120 ? text : text[..120] + "…") : who + " wrote in " + where;
}
