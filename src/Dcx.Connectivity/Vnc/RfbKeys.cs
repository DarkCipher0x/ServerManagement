namespace DcxManager.Vnc;

/// <summary>X11 keysyms that RFB key events carry (RFC 6143 §7.5.4). The screen maps its own keys to these; text goes
/// through <see cref="ForChar"/>.</summary>
public static class RfbKeys
{
    public const uint BackSpace = 0xff08, Tab = 0xff09, Return = 0xff0d, Escape = 0xff1b, Insert = 0xff63, Delete = 0xffff;
    public const uint Home = 0xff50, Left = 0xff51, Up = 0xff52, Right = 0xff53, Down = 0xff54, PageUp = 0xff55, PageDown = 0xff56, End = 0xff57;
    public const uint ShiftL = 0xffe1, ShiftR = 0xffe2, ControlL = 0xffe3, ControlR = 0xffe4, AltL = 0xffe9, AltR = 0xffea;
    /// <summary>The Windows key: Super, which macOS Screen Sharing and most Linux desktops take as Command / Super.</summary>
    public const uint SuperL = 0xffeb, SuperR = 0xffec, Menu = 0xff67;
    public const uint CapsLock = 0xffe5, NumLock = 0xff7f, ScrollLock = 0xff14, Pause = 0xff13, Print = 0xff61;
    public const uint F1 = 0xffbe;   // F1..F12 are consecutive

    /// <summary>The keysym for a typed character: Latin-1 as itself, anything else as 0x01000000 + its code point.</summary>
    public static uint ForChar(int codePoint) => codePoint switch
    {
        '\r' or '\n' => Return,
        '\t' => Tab,
        '\b' => BackSpace,
        >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff => (uint)codePoint,
        _ => 0x01000000u + (uint)codePoint,
    };
}
