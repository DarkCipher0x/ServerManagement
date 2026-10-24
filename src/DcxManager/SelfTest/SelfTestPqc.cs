using System.IO;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Library;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager;

/// <summary>
/// ADR-090 (0.6.12, DarkCipher0x: "implement post-quantum across the entire app"). The primitives (ML-KEM-768, ML-DSA-65),
/// the shared library's hybrid keys, wraps and signatures (with downgrade and tamper attempts, and the older classical keys
/// still working), and the SSH key exchange order. The update manifest's second signature is checked with the other update
/// tests (SelfTest.UpdatesAndServices); the library end to end (with hybrid keys) by LibraryTests. No network, no window.
/// </summary>
public static class PqcTests
{
    public static void Run(string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. The primitives.
        try
        {
            if (!Pqc.Works()) throw new Exception("ML-KEM / ML-DSA self-check failed");
            var (seed, pub) = Pqc.KemGenerate();
            var (ct, ss) = Pqc.KemEncapsulate(pub);
            if (seed.Length != 64 || pub.Length != 1184 || ct.Length != 1088 || ss.Length != 32) throw new Exception($"ML-KEM-768 sizes {seed.Length}/{pub.Length}/{ct.Length}/{ss.Length}");
            var bad = (byte[])ct.Clone(); bad[10] ^= 1;
            if (Pqc.KemDecapsulate(seed, bad).AsSpan().SequenceEqual(ss)) throw new Exception("a changed ML-KEM ciphertext gave the same secret");
            var (sseed, spub) = Pqc.SigGenerate();
            var sig = Pqc.Sign(sseed, Encoding.UTF8.GetBytes("m"));
            if (sseed.Length != 32 || spub.Length != 1952 || sig.Length != 3309) throw new Exception($"ML-DSA-65 sizes {sseed.Length}/{spub.Length}/{sig.Length}");
            var tsig = (byte[])sig.Clone(); tsig[100] ^= 1;
            if (Pqc.Verify(spub, Encoding.UTF8.GetBytes("m"), tsig) || Pqc.Verify(spub, Encoding.UTF8.GetBytes("n"), sig)) throw new Exception("a changed ML-DSA signature or message verified");
            var k1 = Pqc.Combine(new byte[32], ss, new byte[] { 1 }, "a");
            if (k1.AsSpan().SequenceEqual(Pqc.Combine(new byte[32], ss, new byte[] { 1 }, "b")) || k1.AsSpan().SequenceEqual(Pqc.Combine(new byte[32], new byte[32], new byte[] { 1 }, "a")))
                throw new Exception("the hybrid combiner ignores its context or the post-quantum secret");
            if (Pqc.Split("pq1:AA.BB") is not ("AA", "BB") || Pqc.Split("AA") != null || Pqc.Split("pq1:AA") != null || Pqc.Split("pq1:A.B.C") != null) throw new Exception("pq1: parsing");
            ok("0.6.12 post-quantum primitives (BouncyCastle 2.7.0, the version SSH.NET already brings): ML-KEM-768 (64-byte seed, 1,184-byte public key, 1,088-byte ciphertext, 32-byte secret; a changed ciphertext gives another secret) and ML-DSA-65 (1,952-byte public key, 3,309-byte signature; a changed signature or message fails); the hybrid combiner binds both secrets and the context");
        }
        catch (Exception ex) { fail("post-quantum primitives", ex); }

        // 2. The shared library's hybrid keys.
        try
        {
            using var pq = MemberKeys.Create();
            using var classic = MemberKeys.Create(postQuantum: false);
            if (!pq.PostQuantum || classic.PostQuantum || !LibraryCrypto.IsPostQuantum(pq.EncPub) || !LibraryCrypto.IsPostQuantum(pq.SigPub) || LibraryCrypto.IsPostQuantum(classic.EncPub))
                throw new Exception("new members don't get hybrid keys");
            var (e, s) = pq.ExportPrivate();
            using var back = MemberKeys.Import(e, s);
            if (back.EncPub != pq.EncPub || back.SigPub != pq.SigPub || !back.PostQuantum) throw new Exception("hybrid private keys don't survive export / import");
            var secret = LibraryCrypto.RandomKey();
            var w = LibraryCrypto.WrapFor(pq.EncPub, secret, "ctx");
            if (w.Kem.Length == 0 || !LibraryCrypto.Unwrap(back.Enc, w, "ctx").AsSpan().SequenceEqual(secret)) throw new Exception("hybrid wrap round trip");
            void Refused(string what, Action a) { try { a(); } catch (CryptographicException) { return; } throw new Exception(what); }
            Refused("a wrap opened under another context", () => LibraryCrypto.Unwrap(pq.Enc, w, "other"));
            Refused("a classical wrap was accepted by a post-quantum key (downgrade)", () => LibraryCrypto.Unwrap(pq.Enc, LibraryCrypto.WrapFor(Pqc.Split(pq.EncPub)!.Value.Classical, secret, "ctx"), "ctx"));
            Refused("a changed ML-KEM ciphertext opened the wrap", () => LibraryCrypto.Unwrap(pq.Enc, new Wrap { Epk = w.Epk, Ct = w.Ct, Kem = Flip(w.Kem) }, "ctx"));
            Refused("a wrap without its ML-KEM part opened", () => LibraryCrypto.Unwrap(pq.Enc, new Wrap { Epk = w.Epk, Ct = w.Ct }, "ctx"));
            // A tampered share's malformed fields are crypto errors (the item is skipped), never another exception type.
            Refused("a malformed ML-KEM field wasn't a crypto error", () => LibraryCrypto.Unwrap(pq.Enc, new Wrap { Epk = w.Epk, Ct = w.Ct, Kem = "not base64 !!" }, "ctx"));
            Refused("a short ML-KEM field wasn't a crypto error", () => LibraryCrypto.Unwrap(pq.Enc, new Wrap { Epk = w.Epk, Ct = w.Ct, Kem = Convert.ToBase64String(new byte[12]) }, "ctx"));
            Refused("a malformed recipient key wasn't a crypto error", () => LibraryCrypto.WrapFor(Pqc.Join(Pqc.Split(pq.EncPub)!.Value.Classical, Convert.ToBase64String(new byte[40])), secret, "ctx"));
            var cw = LibraryCrypto.WrapFor(classic.EncPub, secret, "ctx");
            if (cw.Kem.Length != 0 || !LibraryCrypto.Unwrap(classic.Enc, cw, "ctx").AsSpan().SequenceEqual(secret)) throw new Exception("classical members (joined before 0.6.12) no longer work");

            var sig = pq.Sign("record");
            if (!LibraryCrypto.Verify(pq.SigPub, "record", sig) || LibraryCrypto.Verify(pq.SigPub, "other", sig)) throw new Exception("hybrid signature");
            if (LibraryCrypto.Verify(pq.SigPub, "record", Pqc.Split(sig)!.Value.Classical)) throw new Exception("a hybrid signer's ECDSA-only signature was accepted (downgrade)");
            using var other = MemberKeys.Create();
            if (LibraryCrypto.Verify(pq.SigPub, "record", Pqc.Join(Pqc.Split(sig)!.Value.Classical, Pqc.Split(other.Sign("record"))!.Value.PostQuantum)))
                throw new Exception("a signature with another key's ML-DSA half was accepted");
            if (!LibraryCrypto.Verify(classic.SigPub, "record", classic.Sign("record")) || LibraryCrypto.Verify(classic.SigPub, "record", sig)) throw new Exception("classical signatures");

            using (var oldAudit = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            using (var k1 = KemKey.ImportBytes(oldAudit.ExportPkcs8PrivateKey()))
                if (k1.PostQuantum) throw new Exception("an audit key from before 0.6.12 didn't import as classical");
            using (var fresh = KemKey.Create())
            using (var k2 = KemKey.ImportBytes(Encoding.UTF8.GetBytes(fresh.ExportPrivate())))
                if (!k2.PostQuantum || k2.Pub != fresh.Pub) throw new Exception("a hybrid audit key didn't survive being wrapped as bytes");
            if (LibraryCrypto.Fingerprint(pq.EncPub, pq.SigPub) == LibraryCrypto.Fingerprint(Pqc.Split(pq.EncPub)!.Value.Classical, Pqc.Split(pq.SigPub)!.Value.Classical))
                throw new Exception("the fingerprint doesn't cover the post-quantum halves");
            ok("0.6.12 shared library post-quantum: new members and audit keys are hybrid (P-256 + ML-KEM-768 to wrap, P-256 + ML-DSA-65 to sign) and survive export / import; a hybrid wrap opens only with its context, a classical wrap for a hybrid key, a changed or missing ML-KEM part are refused; a hybrid signer's ECDSA-only signature and a mixed signature are refused; members and audit keys from before 0.6.12 still work; fingerprints cover the post-quantum halves");
        }
        catch (Exception ex) { fail("shared library post-quantum keys", ex); }

        // 3. SSH key exchange: post-quantum first; with the requirement, only post-quantum.
        try
        {
            Renci.SshNet.ConnectionInfo Ci() => new("127.0.0.1", 22, "demo", new Renci.SshNet.PasswordAuthenticationMethod("demo", "x"));
            var prefer = Ci();
            SshPq.Apply(prefer, require: false);
            var order = prefer.KeyExchangeAlgorithms.Keys.ToList();
            if (order.Count < 4 || order[0] != "mlkem768x25519-sha256" || !SshPq.IsPostQuantum(order[1]) || !order.Contains("curve25519-sha256"))
                throw new Exception("preferred order: " + string.Join(", ", order));
            var require = Ci();
            SshPq.Apply(require, require: true);
            if (require.KeyExchangeAlgorithms.Count == 0 || require.KeyExchangeAlgorithms.Keys.Any(k => !SshPq.IsPostQuantum(k))) throw new Exception("required: " + string.Join(", ", require.KeyExchangeAlgorithms.Keys));
            if (!SshPq.Describe("mlkem768x25519-sha256").StartsWith("post-quantum") || !SshPq.Describe("curve25519-sha256").StartsWith("classical")) throw new Exception("strip label");
            if (SshPq.RequirementProblem(new Exception("No matching key exchange algorithm"), true) == null || SshPq.RequirementProblem(new Exception("No matching key exchange algorithm"), false) != null)
                throw new Exception("the requirement's error message");
            if (!ProtectedSettings.Items.Any(i => i.Key == "net.pqSshRequired") || PolicySettings.Catalog.All(c => c.Name != nameof(AppSettings.PqSshRequired))) throw new Exception("the requirement isn't admin-protected and a policy");
            ok($"0.6.12 SSH post-quantum key exchange: every connection offers {string.Join(", ", order.Where(SshPq.IsPostQuantum))} first (then {order.Count(k => !SshPq.IsPostQuantum(k))} classical); \"Require post-quantum key exchange for SSH\" (admin-protected, policy PqSshRequired) leaves only those; the session strip names what was negotiated");
        }
        catch (Exception ex) { fail("SSH post-quantum key exchange", ex); }
    }

    private static string Flip(string b64) { var b = Convert.FromBase64String(b64); b[5] ^= 1; return Convert.ToBase64String(b); }
}
