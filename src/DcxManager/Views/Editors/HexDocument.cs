using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Views.Editors;

/// <summary>
/// The bytes a hex editor works on (0.3.0 §13): overwrite, insert and delete, undo / redo, find a byte pattern or text,
/// and selection facts (length, CRC-32, SHA-256, the value as integers). Pure logic, in memory only (remote files are never
/// written to local disk), capped at <see cref="MaxBytes"/>.
/// </summary>
public sealed class HexDocument
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private readonly List<byte> _b;
    private readonly Stack<(int At, byte[] Removed, byte[] Added)> _undo = new(), _redo = new();

    public HexDocument(byte[] data)
    {
        if (data.Length > MaxBytes) throw new ArgumentException($"the hex editor opens files up to {MaxBytes / (1024 * 1024)} MB");
        _b = new List<byte>(data);
    }

    public int Length => _b.Count;
    public byte this[int i] => _b[i];
    public bool Dirty => _undo.Count > 0;
    public byte[] ToArray() => _b.ToArray();
    public event Action? Changed;

    private void Apply(int at, int remove, byte[] add, bool record = true)
    {
        var removed = _b.GetRange(at, remove).ToArray();
        _b.RemoveRange(at, remove);
        _b.InsertRange(at, add);
        if (record) { _undo.Push((at, removed, add)); _redo.Clear(); }
        Changed?.Invoke();
    }

    public void Overwrite(int at, byte value)
    {
        if (at < 0 || at > _b.Count) return;
        if (at == _b.Count) Apply(at, 0, [value]); else Apply(at, 1, [value]);
    }

    public void Insert(int at, byte[] bytes) { if (at >= 0 && at <= _b.Count && _b.Count + bytes.Length <= MaxBytes) Apply(at, 0, bytes); }
    public void Delete(int at, int count) { if (at >= 0 && count > 0 && at + count <= _b.Count) Apply(at, count, []); }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var (at, removed, added) = _undo.Pop();
        Apply(at, added.Length, removed, record: false);
        _redo.Push((at, removed, added));
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var (at, removed, added) = _redo.Pop();
        Apply(at, removed.Length, added, record: false);
        _undo.Push((at, removed, added));
        return true;
    }

    /// <summary>"DE AD be ef" (hex bytes) or plain text (UTF-8) → the bytes to look for; null when it's neither.</summary>
    public static byte[]? Pattern(string text, bool asHex)
    {
        if (!asHex) return text.Length == 0 ? null : Encoding.UTF8.GetBytes(text);
        var h = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (h.Length == 0 || h.Length % 2 != 0 || !h.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(h);
    }

    /// <summary>The next (or previous) position of <paramref name="pattern"/> from <paramref name="from"/>, wrapping; −1 if none.</summary>
    public int Find(byte[] pattern, int from, bool back = false)
    {
        if (pattern.Length == 0 || pattern.Length > _b.Count) return -1;
        bool At(int i) { for (int k = 0; k < pattern.Length; k++) if (_b[i + k] != pattern[k]) return false; return true; }
        int last = _b.Count - pattern.Length;
        if (!back)
        {
            for (int i = Math.Max(0, from); i <= last; i++) if (At(i)) return i;
            for (int i = 0; i < Math.Min(from, last + 1); i++) if (At(i)) return i;
        }
        else
        {
            for (int i = Math.Min(from, last); i >= 0; i--) if (At(i)) return i;
            for (int i = last; i > from; i--) if (At(i)) return i;
        }
        return -1;
    }

    /// <summary>Length, CRC-32 and SHA-256 of a range, and its first bytes as 8/16/32-bit integers (little-endian).</summary>
    public string Describe(int start, int length)
    {
        start = Math.Clamp(start, 0, _b.Count);
        length = Math.Clamp(length, 0, _b.Count - start);
        var bytes = _b.GetRange(start, length).ToArray();
        var sb = new StringBuilder($"offset 0x{start:X} ({start:N0}) · length {length:N0}");
        if (length > 0)
        {
            sb.Append($" · CRC-32 {Crc32(bytes):X8} · SHA-256 {Convert.ToHexString(SHA256.HashData(bytes))[..16]}…");
        }
        if (start < _b.Count)
        {
            sb.Append($" · u8 {_b[start]} · i8 {(sbyte)_b[start]}");
            if (start + 2 <= _b.Count) sb.Append($" · u16 {BitConverter.ToUInt16([_b[start], _b[start + 1]], 0)}");
            if (start + 4 <= _b.Count) sb.Append($" · u32 {BitConverter.ToUInt32(_b.GetRange(start, 4).ToArray(), 0)}");
        }
        return sb.ToString();
    }

    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
