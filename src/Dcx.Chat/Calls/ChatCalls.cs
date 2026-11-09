using System.Windows;
using CipherChat.Engine;

namespace DcxManager.Chat.Calls;

/// <summary>
/// Calls for an unlocked chat (0.6.38, ADR-117): the engine's ringing turns into the ring popup and its "ring stopped"
/// (answered or declined on another device, missed, hung up) takes it down; locking the chat ends its ring and its call.
/// The engine is CipherChat's own (it verifies every signal); this only shows it.
/// </summary>
internal static class ChatCalls
{
    /// <summary>A chat was unlocked: its calls ring here.</summary>
    public static void Attach(ChatSession s, ChatEngine e)
    {
        e.Calls.Ringing += inc => OnUi(() => { if (ReferenceEquals(s.Engine, e)) RingPopup.Show(s, inc); });
        e.Calls.RingStopped += (call, _) => OnUi(() => RingPopup.Stop(call));
    }

    /// <summary>A chat is being locked: its ring stops and its call ends.</summary>
    public static void Detach(ChatSession s) => OnUi(() => { RingPopup.StopFor(s); CallWindow.EndFor(s); });

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
