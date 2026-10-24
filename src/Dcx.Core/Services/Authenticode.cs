using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DcxManager.Services;

/// <summary>
/// Authenticode checks of a file on disk (moved out of Updater in 0.6.14 so the Bitwarden CLIs are checked the same way
/// as update installers): WinVerifyTrust with whole-chain revocation and no UI, and the signer's certificate.
/// </summary>
public static class Authenticode
{
    /// <summary>TRUST_E_NOSIGNATURE: the file carries no signature.</summary>
    public const int NotSigned = unchecked((int)0x800B0100);
    /// <summary>CRYPT_E_REVOCATION_OFFLINE: revocation couldn't be checked (no network, nothing cached).</summary>
    public const int RevocationOffline = unchecked((int)0x80092013);

    /// <summary>0 when the signature is valid and its chain trusted; otherwise WinVerifyTrust's error. Revocation is
    /// checked online when <paramref name="online"/>, else from cached CRL / OCSP data only (never the network).</summary>
    public static int Verify(string path, bool online)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* WTD_UI_NONE */, dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0,
                // whole-chain revocation (excluding the root); offline mode = cached CRL/OCSP only, never the network
                fdwRevocationChecks = 1 /* WTD_REVOKE_WHOLECHAIN */,
                dwProvFlags = 0x00000080 /* WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT */ | (online ? 0 : 0x00001000 /* WTD_CACHE_ONLY_URL_RETRIEVAL */),
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    /// <summary>The certificate that signed the file, or null when it isn't signed. Call it after <see cref="Verify"/>
    /// succeeded: it reads the signer, it doesn't check it.</summary>
    public static X509Certificate2? Signer(string path)
    {
        // CreateFromSignedFile is obsolete in .NET 9+ with no loader replacement; reading the Authenticode signer of a file
        // WinVerifyTrust has just verified is the use it is still meant for.
#pragma warning disable SYSLIB0057
        try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
#pragma warning restore SYSLIB0057
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
