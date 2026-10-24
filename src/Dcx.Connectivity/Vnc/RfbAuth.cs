using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Vnc;

/// <summary>
/// The RFB sign-in methods (0.5.1 #19, ADR-069) and which one to pick. The order puts encryption first: on a direct network
/// connection VeNCrypt X.509 (TLS) wins, then Apple Remote Desktop (the credentials are encrypted, the session isn't), then
/// the VNC password (a DES challenge; the session isn't encrypted) and finally no password. Inside an SSH tunnel the
/// stream is already encrypted, so the server's own choice matters less and VeNCrypt Plain is allowed too.
/// The ciphers here (DES, MD5, AES-ECB) are what the protocol defines; they protect only the sign-in, never the stream —
/// which is why the app tunnels VNC through SSH by default.
/// </summary>
internal static class RfbAuth
{
    public const int None = 1, Vnc = 2, VeNCrypt = 19, Ard = 30;

    public static int? Choose(IReadOnlyList<int> offered, RfbOptions o)
    {
        bool user = o.Username.Length > 0;
        int[] order = o.TransportSecure
            ? (user ? new[] { Ard, VeNCrypt, Vnc, None } : new[] { Vnc, VeNCrypt, None })
            : (user ? new[] { VeNCrypt, Ard, Vnc, None } : new[] { VeNCrypt, Vnc, None });
        foreach (var t in order) if (offered.Contains(t)) return t;
        return null;
    }

    public static string NoneSuitable(IReadOnlyList<int> offered, RfbOptions o)
    {
        if (offered.Contains(Ard) && o.Username.Length == 0)
            return "This Mac signs in with a macOS user name and password (Apple Remote Desktop). Give the host a credential with the user name, or switch on “VNC viewers may control screen with password” in the Mac's Screen Sharing settings.";
        return $"The server offers only security types this app doesn't support ({string.Join(", ", offered.Select(Name))}).";
    }

    public static int? ChooseVeNCrypt(IReadOnlyList<int> subs, RfbOptions o)
    {
        bool user = o.Username.Length > 0;
        var order = new List<int>();
        if (user) order.Add(262);
        order.AddRange(new[] { 261, 260 });
        if (o.TransportSecure && user) order.Add(256);   // a password in clear: only inside the SSH tunnel
        order.AddRange(new[] { Vnc, None });
        foreach (var t in order) if (subs.Contains(t)) return t;
        return null;
    }

    public static string NoneSuitableVeNCrypt(IReadOnlyList<int> subs, RfbOptions o) =>
        subs.Contains(256) && !o.TransportSecure
            ? "The server would take the password in clear (VeNCrypt Plain without TLS). Connect through an SSH tunnel instead (host editor → VNC → Through SSH)."
            : subs.Any(s => s is 257 or 258 or 259)
                ? "The server offers only anonymous TLS (VeNCrypt TLSNone/TLSVnc/TLSPlain), which Windows doesn't support and which can't prove who the server is. Give the server an X.509 certificate (x509 security types) or connect through an SSH tunnel."
                : $"The server offers only VeNCrypt sub-types this app doesn't support ({string.Join(", ", subs)}).";

    public static string Name(int t) => t switch
    {
        None => "none", Vnc => "VNC password", VeNCrypt => "VeNCrypt", Ard => "Apple Remote Desktop",
        5 => "RA2", 6 => "RA2ne", 16 => "Tight", 18 => "TLS", 33 or 35 or 36 => $"Apple ({t})", _ => t.ToString(),
    };

    /// <summary>VNC password (type 2): the 16-byte challenge encrypted with DES, the key being the password's first eight
    /// characters with each byte's bits reversed.</summary>
    public static void VncPassword(RfbReader r, RfbClient c, string password)
    {
        var challenge = r.Bytes(16);
        if (password.Length == 0) throw new RfbException("The server asks for a VNC password: give the host a password credential.");
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < Math.Min(8, pw.Length); i++) key[i] = Reverse(pw[i]);
        byte[] response;
        try
        {
#pragma warning disable CA5351 // RFB type 2 is defined as DES (RFC 6143 §7.2.2); it protects only this sign-in, and the app tunnels the stream through SSH by default.
            using var des = DES.Create();
#pragma warning restore CA5351
            des.Key = key;
            response = des.EncryptEcb(challenge, PaddingMode.None);
        }
        catch (CryptographicException) { throw new RfbException("This password can't be used for VNC sign-in (it makes a weak DES key); choose another one on the server."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pw); }
        c.WriteRaw(response);
    }

    private static byte Reverse(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) r |= 1 << (7 - i);
        return (byte)r;
    }

    /// <summary>Apple Remote Desktop (type 30): Diffie-Hellman with the server's generator and prime, MD5 of the shared
    /// secret as an AES-128 key, and the macOS user name and password (64 bytes each, random-padded) encrypted with it.</summary>
    public static void AppleRemoteDesktop(RfbReader r, RfbClient c, string user, string password)
    {
        int g = r.U16(), len = r.U16();
        if (len is < 16 or > 1024) throw new RfbException($"The Mac sent an unusable key length ({len}).");
        var primeBytes = r.Bytes(len);
        var serverPublic = r.Bytes(len);
        var p = new BigInteger(primeBytes, isUnsigned: true, isBigEndian: true);
        var ys = new BigInteger(serverPublic, isUnsigned: true, isBigEndian: true);
        if (g < 2 || p.IsEven || ys <= BigInteger.One || ys >= p - BigInteger.One) throw new RfbException("The Mac sent invalid Diffie-Hellman parameters.");
        if (user.Length == 0) throw new RfbException("This Mac signs in with a macOS user name and password: give the host a credential with the user name.");

        var privBytes = RandomNumberGenerator.GetBytes(len);
        var x = new BigInteger(privBytes, isUnsigned: true, isBigEndian: true) % (p - 2) + 1;
        var clientPublic = BigInteger.ModPow(g, x, p);
        var shared = Pad(BigInteger.ModPow(ys, x, p), len);
        var creds = RandomNumberGenerator.GetBytes(128);
        byte[] key = Array.Empty<byte>(), u = Encoding.UTF8.GetBytes(user), pw = Encoding.UTF8.GetBytes(password);
        try
        {
            if (u.Length > 63 || pw.Length > 63) throw new RfbException("Apple Remote Desktop allows at most 63 bytes of user name and of password.");
            u.CopyTo(creds, 0); creds[u.Length] = 0;
            pw.CopyTo(creds, 64); creds[64 + pw.Length] = 0;
#pragma warning disable CA5351 // Apple Remote Desktop derives its AES key with MD5 (protocol-defined); it protects only the sign-in.
            key = MD5.HashData(shared);
#pragma warning restore CA5351
            using var aes = Aes.Create();
            aes.Key = key;
#pragma warning disable CA5358 // ECB is what Apple Remote Desktop specifies for this one 128-byte block.
            var enc = aes.EncryptEcb(creds, PaddingMode.None);
#pragma warning restore CA5358
            var reply = new byte[128 + len];
            enc.CopyTo(reply, 0);
            Pad(clientPublic, len).CopyTo(reply, 128);
            c.WriteRaw(reply);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(creds); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(privBytes); CryptographicOperations.ZeroMemory(pw);
        }
    }

    private static byte[] Pad(BigInteger v, int len)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (b.Length == len) return b;
        if (b.Length > len) throw new RfbException("Diffie-Hellman value longer than the key length.");
        var o = new byte[len];
        b.CopyTo(o, len - b.Length);
        return o;
    }

    /// <summary>VeNCrypt Plain: lengths then user name and password (UTF-8). Only ever inside TLS or an SSH tunnel.</summary>
    public static void Plain(RfbClient c, string user, string password)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(password);
        var b = new byte[8 + u.Length + p.Length];
        try
        {
            RfbClient.BE32(b, 0, u.Length); RfbClient.BE32(b, 4, p.Length);
            u.CopyTo(b, 8); p.CopyTo(b, 8 + u.Length);
            c.WriteRaw(b);
        }
        finally { CryptographicOperations.ZeroMemory(b); CryptographicOperations.ZeroMemory(p); }
    }
}
