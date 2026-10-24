using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>The AI Engineer sidebar in its own window (movable, maximisable, any monitor). It borrows the one
/// <see cref="AiPanel"/>; closing it hands the panel back to the dock, collapsed, with the conversation intact.</summary>
public sealed class AiWindow : DetachedWindow
{
    private readonly Border _slot;
    private readonly Action<AiWindow> _closedByUser;
    public AiPanel Panel { get; }

    public AiWindow(Window? owner, AiPanel panel, Action<AiWindow> closedByUser) : base(owner, new Border(), 560, 780)
    {
        _slot = (Border)Content;
        _closedByUser = closedByUser;
        Panel = panel;
        MinWidth = 360; MinHeight = 420;
        Title = "✦ AI Engineer — DarkCipher ServerManagement";
        _slot.Child = panel;
    }

    /// <summary>Lets go of the panel so the dock can parent it again (a WPF element has one parent).</summary>
    public void Release() => _slot.Child = null;

    protected override void OnUserClosing() => _closedByUser(this);
}
