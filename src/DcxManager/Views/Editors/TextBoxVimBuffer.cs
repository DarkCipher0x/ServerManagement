using System.Windows.Controls;

namespace DcxManager.Views.Editors;

/// <summary>The editor's TextBox as a <see cref="IVimBuffer"/>: replacements go through the selection so each one is an
/// undo step of the TextBox's own undo stack (u / Ctrl+R in Vim, Ctrl+Z / Ctrl+Y otherwise). In visual mode the caret is
/// tracked here, since a TextBox shows its caret at the end of the selection.</summary>
public sealed class TextBoxVimBuffer(TextBox box) : IVimBuffer
{
    private int? _caret;

    public string Text => box.Text;

    public int Caret
    {
        get => _caret ?? box.CaretIndex;
        set
        {
            var v = Math.Clamp(value, 0, box.Text.Length);
            if (box.SelectionLength > 0) { _caret = v; return; }
            _caret = null;
            box.CaretIndex = v;
        }
    }

    public void Select(int start, int length)
    {
        start = Math.Clamp(start, 0, box.Text.Length);
        length = Math.Clamp(length, 0, box.Text.Length - start);
        box.Select(start, length);
        if (length == 0) _caret = null;
    }

    public void Replace(int start, int length, string text)
    {
        _caret = null;
        start = Math.Clamp(start, 0, box.Text.Length);
        length = Math.Clamp(length, 0, box.Text.Length - start);
        box.Select(start, length);
        box.SelectedText = text;
        box.Select(start + text.Length, 0);
    }

    public void Undo() { _caret = null; box.Undo(); }
    public void Redo() { _caret = null; box.Redo(); }
}

/// <summary>A plain string as a buffer (the self-test drives the Vim engine with it).</summary>
public sealed class StringVimBuffer(string text) : IVimBuffer
{
    private readonly Stack<(string Text, int Caret)> _undo = new(), _redo = new();
    public string Text { get; private set; } = text;
    public int Caret { get; set; }
    public int SelStart { get; private set; }
    public int SelLength { get; private set; }
    public void Select(int start, int length) { SelStart = start; SelLength = length; }
    public void Replace(int start, int length, string t) { _undo.Push((Text, Caret)); _redo.Clear(); Text = Text[..start] + t + Text[(start + length)..]; Caret = start + t.Length; }
    public void Undo() { if (_undo.Count == 0) return; _redo.Push((Text, Caret)); (Text, Caret) = _undo.Pop(); }
    public void Redo() { if (_redo.Count == 0) return; _undo.Push((Text, Caret)); (Text, Caret) = _redo.Pop(); }
}
