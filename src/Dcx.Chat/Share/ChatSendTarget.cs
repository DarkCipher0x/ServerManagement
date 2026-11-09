using System.Windows.Controls;
using CipherChat.Engine;
using DcxManager.Chat.Views;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Share;

/// <summary>
/// The screens' "💬 Send to a chat…" (ADR-117). You pick a room from every unlocked chat; the room opens as a tab with
/// the text in the composer and the files attached, and nothing is sent until you press Send there — so you always see
/// (and can edit or drop) exactly what will go. Policy <c>ChatShareFromScreens</c> turns it off; <c>ChatAllowFiles</c> and
/// <c>ChatMaxFileMB</c> apply to the files. Only where it came from is audited, never the content.
/// </summary>
internal sealed class ChatSendTarget : ISendTarget
{
    public const int MaxText = 16_000;

    public string? WhyNot() => ChatPolicy.ShareBlocked();

    public long MaxFileBytes => ChatPolicy.MaxFileBytes;

    public Task SendAsync(SendItem item)
    {
        if (WhyNot() is { } why) { Dialogs.Info("Send to a chat", why); return Task.CompletedTask; }
        var rooms = Rooms();
        if (rooms.Count == 0)
        {
            Dialogs.Info("Send to a chat", "No chat is unlocked. Open Chats, unlock your chat (or pair this PC), then send it again.");
            ChatModule.OpenHome();
            return Task.CompletedTask;
        }
        var (text, files, notes) = Prepare(item, ChatPolicy.FilesBlocked() == null, ChatPolicy.MaxFileBytes);
        if (text.Length == 0 && files.Count == 0) { Dialogs.Info("Send to a chat", string.Join("\n", notes.DefaultIfEmpty("Nothing to send."))); return Task.CompletedTask; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick: send to a chat"); return Task.CompletedTask; }
        var note = notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "";
        if (Dialogs.Pick("Send to a chat", $"Where should “{ChatRoomView.Clean(item.Subject)}” go? The room opens with it ready to review; nothing is sent until you press Send." + note,
                rooms, r => r.Label) is not { } to) return Task.CompletedTask;
        if (to.Session.Engine is null) { Dialogs.Info("Send to a chat", "That chat was locked in the meantime."); return Task.CompletedTask; }
        if (ModuleTabs.Open(ChatRoomView.TabKey(to.Session, to.Room), "💬 " + to.Room.Label(to.Session.Engine.MyUser), () => new ChatRoomView(to.Session, to.Room)) is TabItem { Content: ChatRoomView view })
        {
            foreach (var f in files) view.AddFile(f.Name, f.Bytes, f.Mime);
            if (text.Length > 0) view.Prefill(text);
        }
        AuditLog.Write("you", "chat", "prepared to send from a screen", ChatRoomView.Clean(item.Subject) + " → " + to.Room.Label(to.Session.Engine.MyUser) + $" ({files.Count} file(s))");
        return Task.CompletedTask;
    }

    internal sealed record RoomChoice(ChatSession Session, Room Room, string Label);

    internal static List<RoomChoice> Rooms()
    {
        var ready = ChatHub.Sessions.Where(s => s.Engine != null).ToList();
        bool many = ready.Count > 1;
        return ready.SelectMany(s => s.Engine!.Rooms.Select(r => new RoomChoice(s, r, ChatRoomView.Clean(r.Label(s.Engine!.MyUser)) + (many ? " · " + s.Display : ""))))
            .OrderBy(c => c.Room.Kind == "dm").ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What goes into the composer and the attachments, under this PC's rules (the self-test checks them): text
    /// over the message limit becomes a .txt file when files are allowed (else it is cut, said in a note); files are
    /// dropped when files are off or when larger than the limit, each with a note.</summary>
    internal static (string Text, List<SendFile> Files, List<string> Notes) Prepare(SendItem item, bool filesAllowed, long maxBytes)
    {
        var notes = new List<string>();
        var text = item.Text.TrimEnd();
        var files = new List<SendFile>();
        if (text.Length > MaxText)
        {
            if (filesAllowed && System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                files.Add(new SendFile(SafeName(item.Subject) + ".txt", System.Text.Encoding.UTF8.GetBytes(text), "text/plain"));
                text = "";
                notes.Add("The text is long, so it is attached as a .txt file.");
            }
            else
            {
                text = text[..MaxText] + "\n…";
                notes.Add($"The text was cut to {MaxText:N0} characters (the message limit).");
            }
        }
        foreach (var f in item.Files)
        {
            if (!filesAllowed) { notes.Add($"{f.Name} isn't attached: files in chats are turned off on this PC."); continue; }
            if (f.Bytes.LongLength > maxBytes) { notes.Add($"{f.Name} isn't attached: it is larger than {maxBytes >> 20} MB."); continue; }
            files.Add(f with { Name = SafeName(f.Name, keepExtension: true) });
        }
        return (text, files, notes);
    }

    /// <summary>A file name made from a subject: no path or reserved characters, at most 80 characters.</summary>
    internal static string SafeName(string s, bool keepExtension = false)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var name = new string(s.Select(c => bad.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim().Trim('.');
        if (name.Length == 0) name = "shared";
        if (name.Length <= 80) return name;
        var ext = keepExtension ? System.IO.Path.GetExtension(name) : "";
        return name[..(80 - ext.Length)] + ext;
    }
}
