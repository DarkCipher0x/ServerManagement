using System.IO;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Import;

/// <summary>Turns the ticked connections into hosts in the encrypted store (0.5.0, ADR-061).</summary>
public static class ConnectionImportApply
{
    public sealed record Outcome(int Added, int Skipped, int Passwords, int Keys, IReadOnlyList<string> Notes);

    /// <summary>
    /// Adds <paramref name="picked"/> under <paramref name="rootFolder"/> (each keeps its source folder below it). A host
    /// already here (same address, port and user) is skipped. Passwords become vault credentials only when
    /// <paramref name="passwords"/> is on; a key file is read (64 KB at most) into a key credential when it exists. Jump
    /// hosts named by the source are linked when a host of that name exists. Saves once. Throws when imports are off.
    /// </summary>
    public static Outcome Apply(IEnumerable<ImportedHost> picked, string source, string rootFolder, bool passwords)
    {
        if (BackupPolicy.ImportBlocked() is { } no) throw new InvalidOperationException(no);
        var data = AppData.Current;
        var root = ConnectionImport.CleanFolder(rootFolder);
        int added = 0, skipped = 0, pwCount = 0, keyCount = 0;
        var notes = new List<string>();
        var made = new List<(HostEntry Host, string? Jump)>();
        foreach (var i in picked)
        {
            var folder = string.Join("/", new[] { root, i.Folder }.Where(f => f.Length > 0));
            if (folder.Length == 0) folder = "General";
            if (data.Hosts.Any(h => h.Address.Equals(i.Address, StringComparison.OrdinalIgnoreCase) && h.Port == i.Port && h.Username == i.Username && h.Protocol == i.Protocol)) { skipped++; continue; }
            var h = new HostEntry
            {
                Name = i.Name, Group = folder, Address = i.Address, Port = i.Port, Username = i.Username, Protocol = i.Protocol,
                Auth = AuthMethod.Password, Notes = (i.Notes.Length > 0 ? i.Notes + "\n" : "") + $"Imported from {source} on {DateTime.Now:yyyy-MM-dd}.",
            };
            if (i.Protocol == "rdp") h.Platform = "windows";
            if (passwords && !string.IsNullOrEmpty(i.Password))
            {
                var c = new Credential { Name = $"{i.Name} ({source})", Kind = CredentialKind.Password, Username = i.Username, Secret = i.Password, Notes = $"Imported from {source}." };
                data.Credentials.Add(c);
                h.CredentialId = c.Id;
                pwCount++;
            }
            else if (i.KeyFile is { } kf && i.Protocol == "ssh" && File.Exists(kf) && new FileInfo(kf).Length <= 64 * 1024)
            {
                try
                {
                    var text = File.ReadAllText(kf);
                    var cred = data.Credentials.FirstOrDefault(c => c.Kind == CredentialKind.PrivateKey && c.Secret.Trim() == text.Trim());
                    if (cred == null)
                    {
                        cred = new Credential { Name = Path.GetFileName(kf), Kind = CredentialKind.PrivateKey, Secret = text, Notes = $"Imported from {source} ({Path.GetFileName(kf)})." };
                        try { KeyTools.Describe(cred); } catch (Exception) { cred.Notes += " Needs its passphrase, or conversion from PuTTY's .ppk: edit the credential."; }
                        data.Credentials.Add(cred);
                        keyCount++;
                    }
                    h.Auth = AuthMethod.PrivateKey; h.CredentialId = cred.Id;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"{i.Name}: the key file couldn't be read ({ex.Message})."); }
            }
            data.Hosts.Add(h);
            made.Add((h, i.Jump));
            added++;
        }
        foreach (var (h, jump) in made)
            if (jump != null && data.FindHostByName(jump) is { } jh && jh.Id != h.Id) h.JumpHostId = jh.Id;
        if (added > 0) AppData.Save();
        // Counts only: never a host name, address or secret in the audit trail.
        AuditLog.Write("you", "store", $"imported connections from {source}", $"{added} added, {skipped} already here, {pwCount} passwords, {keyCount} keys");
        return new Outcome(added, skipped, pwCount, keyCount, notes);
    }
}
