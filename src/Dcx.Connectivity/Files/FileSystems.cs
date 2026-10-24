using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DcxManager.Services;
using DcxManager.Ssh;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace DcxManager.Files;

/// <summary>One entry in a folder listing, the same shape for the local disk and for SFTP.</summary>
public sealed class FsItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public bool IsDrive { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }
    public string Perms { get; init; } = "";
    /// <summary>Unix permission bits (0 when unknown / this PC).</summary>
    public int Mode { get; init; }
    public string Owner { get; init; } = "";
    public bool IsParent { get; init; }
    /// <summary>What the Name column shows when it differs from the name (Find file results: the path below the folder).</summary>
    public string? Display { get; init; }
    /// <summary>A folder's measured size (File commander Ctrl+Space).</summary>
    public long? DirSize { get; init; }

    public string Shown => Display ?? Name;
    public string Icon => IsParent ? "⮤" : IsDrive ? "🖴" : IsDir ? "📁" : IsLink ? "↪" : "📄";
    public string SizeText => IsDir && DirSize is long d ? Fmt.Bytes(d) : IsDir || IsParent ? (IsDrive ? Owner : "") : Fmt.Bytes(Length);

    public FsItem WithDirSize(long bytes) => new()
    {
        Name = Name, FullPath = FullPath, IsDir = IsDir, IsLink = IsLink, IsDrive = IsDrive, Length = Length, Modified = Modified,
        Perms = Perms, Mode = Mode, Owner = Owner, IsParent = IsParent, Display = Display, DirSize = bytes,
    };
    public string ModifiedText => IsParent || IsDrive || Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string Ext => IsDir ? "" : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
}

/// <summary>Where a pane or a transfer points: this PC, or a saved host over SFTP. Serialises as "local" / "host:&lt;id&gt;".</summary>
public sealed record FsRef(string Kind, string? HostId)
{
    public static readonly FsRef Local = new("local", null);
    /// <summary>A pane that isn't connected to anything (0.5.1 #4: a new Commander tab starts here, so nothing is opened —
    /// least of all a server — until you pick a place). Serialises as "none".</summary>
    public static readonly FsRef None = new("none", null);
    public static FsRef ForHost(HostEntry h) => new("host", h.Id);
    public HostEntry? Host => HostId == null ? null : AppData.Current.FindHost(HostId);
    public bool IsNone => Kind == "none";
    public string Title => Kind switch { "local" => "This PC", "none" => "Not connected", _ => Host?.Display ?? "(deleted host)" };
    public override string ToString() => Kind switch { "local" => "local", "none" => "none", _ => "host:" + HostId };
    public static FsRef Parse(string? s) => s != null && s.StartsWith("host:") ? new FsRef("host", s[5..]) : s == "none" ? None : Local;
    public IFileSystem Open() => Kind switch
    {
        "local" => new LocalFs(),
        "none" => new NoneFs(),
        _ => new HostFs(Host ?? throw new InvalidOperationException("That saved host no longer exists.")),
    };
}

/// <summary>The "not connected" place (<see cref="FsRef.None"/>): lists nothing and refuses every operation.</summary>
public sealed class NoneFs : IFileSystem
{
    public const string Why = "This pane isn't connected. Choose This PC or a host first.";
    public FsRef Ref => FsRef.None;
    public string Title => "Not connected";
    public bool IsRemote => false;
    public bool CanAppend => false;
    public Task<string> HomeAsync(CancellationToken ct = default) => Task.FromResult("");
    public Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default) => Task.FromResult(new List<FsItem>());
    public Task<FsItem?> StatAsync(string path, CancellationToken ct = default) => Task.FromResult<FsItem?>(null);
    public Task MkdirAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task DeleteAsync(string path, bool isDir, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task RenameAsync(string from, string to, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task<Stream> CreateAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task ReplaceAsync(string from, string to, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task SetModeAsync(string path, int mode, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public Task<string?> HashAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException(Why);
    public string Join(string dir, string name) => name;
    public string Parent(string path) => "";
    public void Dispose() { }
}

/// <summary>The operations the Commander, the transfer queue and the AI file tools need — nothing more.</summary>
public interface IFileSystem : IDisposable
{
    FsRef Ref { get; }
    string Title { get; }
    bool IsRemote { get; }
    Task<string> HomeAsync(CancellationToken ct = default);
    Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default);
    /// <summary>Null when the path doesn't exist.</summary>
    Task<FsItem?> StatAsync(string path, CancellationToken ct = default);
    Task MkdirAsync(string path, CancellationToken ct = default);
    /// <summary>Deletes a file or a whole folder. Local deletes go to the Recycle Bin.</summary>
    Task DeleteAsync(string path, bool isDir, CancellationToken ct = default);
    Task RenameAsync(string from, string to, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    /// <summary>Creates or truncates the file.</summary>
    Task<Stream> CreateAsync(string path, CancellationToken ct = default);
    string Join(string dir, string name);
    string Parent(string path);

    // ---- transfer support (ADR-014) ----
    /// <summary>False where appending is impossible (SCP): resume then restarts the file.</summary>
    bool CanAppend { get; }
    Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default);
    /// <summary>Renames, replacing an existing target (the final step of every copied file).</summary>
    Task ReplaceAsync(string from, string to, CancellationToken ct = default);
    Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default);
    /// <summary>Unix permission bits; ignored on this PC.</summary>
    Task SetModeAsync(string path, int mode, CancellationToken ct = default);
    /// <summary>Hex SHA-256 computed where the file lives (null = not available cheaply: the caller reads the file).</summary>
    Task<string?> HashAsync(string path, CancellationToken ct = default);
}

// ---- local ------------------------------------------------------------------------------------------------------------

public sealed class LocalFs : IFileSystem
{
    public FsRef Ref => FsRef.Local;
    public string Title => "This PC";
    public bool IsRemote => false;

    public Task<string> HomeAsync(CancellationToken ct = default) => Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<FsItem>();
        if (path.Length == 0)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string label = "";
                try { if (d.IsReady) label = $"{(d.VolumeLabel.Length > 0 ? d.VolumeLabel + " · " : "")}{Fmt.Bytes(d.AvailableFreeSpace)} free"; } catch { }
                list.Add(new FsItem { Name = d.Name.TrimEnd('\\'), FullPath = d.Name, IsDir = true, IsDrive = true, Owner = label + (d.DriveType == DriveType.Network ? " · network" : "") });
            }
            return list;
        }
        var di = new DirectoryInfo(path);
        foreach (var e in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            ct.ThrowIfCancellationRequested();
            bool dir = e.Attributes.HasFlag(FileAttributes.Directory);
            list.Add(new FsItem
            {
                Name = e.Name, FullPath = e.FullName, IsDir = dir, IsLink = e.Attributes.HasFlag(FileAttributes.ReparsePoint),
                Length = e is FileInfo fi ? fi.Length : 0, Modified = e.LastWriteTime,
                Perms = (e.Attributes.HasFlag(FileAttributes.Hidden) ? "h" : "-") + (e.Attributes.HasFlag(FileAttributes.System) ? "s" : "-") + (e.Attributes.HasFlag(FileAttributes.ReadOnly) ? "r" : "-"),
            });
        }
        return list;
    }, ct);

    public Task<FsItem?> StatAsync(string path, CancellationToken ct = default)
    {
        FileSystemInfo? e = Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path) : null;
        return Task.FromResult(e == null ? null : new FsItem
        {
            Name = e.Name, FullPath = e.FullName, IsDir = e is DirectoryInfo, Length = e is FileInfo fi ? fi.Length : 0, Modified = e.LastWriteTime,
        });
    }

    public Task MkdirAsync(string path, CancellationToken ct = default) { Directory.CreateDirectory(path); return Task.CompletedTask; }

    /// <summary>Self-test only: delete for real instead of filling the user's Recycle Bin with scratch files.</summary>
    internal static bool HardDeleteForTests;

    public Task DeleteAsync(string path, bool isDir, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!HardDeleteForTests) { RecycleBin.Send(path); return; }
        if (isDir) Directory.Delete(path, true); else File.Delete(path);
    }, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct = default)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: false));

    public Task<Stream> CreateAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: false));

    public bool CanAppend => true;

    public Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: false));

    public Task ReplaceAsync(string from, string to, CancellationToken ct = default) { File.Move(from, to, overwrite: true); return Task.CompletedTask; }

    public Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default) { File.SetLastWriteTime(path, modified); return Task.CompletedTask; }

    public Task SetModeAsync(string path, int mode, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> HashAsync(string path, CancellationToken ct = default) => Task.Run<string?>(() =>
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }, ct);

    /// <summary>A child inside <paramref name="dir"/>: the name is forced to ONE path segment first (<see cref="SafeChild"/>),
    /// so a name a server sent ("..\..\x", "C:\x", "a/b") can never land outside the folder it was downloaded into.</summary>
    public string Join(string dir, string name) => dir.Length == 0 ? name : Path.Combine(dir, SafeChild(name));

    /// <summary>A file name from somewhere else (an SFTP/SCP listing: on Linux a name may contain '\' or ':' and be "..")
    /// as exactly one Windows path segment: separators, drive colons and invalid characters become '_', "." / ".." too.</summary>
    public static string SafeChild(string name)
    {
        if (name.Length == 0) return "_";
        var bad = Path.GetInvalidFileNameChars();
        var s = string.Concat(name.Select(c => c is '\\' or '/' or ':' || bad.Contains(c) ? '_' : c));
        s = s.TrimEnd(' ', '.');   // "." and ".." end up empty here; "foo." / "foo " are not what Windows would create either
        return s.Length == 0 ? "_" : s;
    }

    public string Parent(string path)
    {
        if (path.Length == 0) return "";
        var p = Path.GetDirectoryName(path.TrimEnd('\\'));
        return p ?? "";   // "C:\" → "" (the drive list)
    }

    public void Dispose() { }
}

/// <summary>Sends a file or folder to the Recycle Bin (undoable), never a hard delete.</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    public static void Send(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI) };
        var rc = SHFileOperationW(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Couldn't move {path} to the Recycle Bin (code 0x{rc:X}).");
    }
}

// ---- SFTP ---------------------------------------------------------------------------------------------------------------

/// <summary>A saved host over SFTP. Connects lazily, reconnects after a drop, and uses the host's jump chain and vault key.</summary>
public sealed class SftpFs : IFileSystem
{
    private readonly HostEntry _host;
    private SshSession<SftpClient>? _session;
    private readonly SemaphoreSlim _connect = new(1, 1);

    public SftpFs(HostEntry host) => _host = host;
    public FsRef Ref => FsRef.ForHost(_host);
    public string Title => _host.Display;
    public bool IsRemote => true;
    public HostEntry Host => _host;

    private async Task<SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_session is { IsConnected: true }) return _session.Client;
        await _connect.WaitAsync(ct);
        try
        {
            if (_session is { IsConnected: true }) return _session.Client;
            _session?.Dispose();
            _session = await SshConnector.ConnectSftpAsync(_host, ct);
            return _session.Client;
        }
        finally { _connect.Release(); }
    }

    public async Task<string> HomeAsync(CancellationToken ct = default) => (await ClientAsync(ct)).WorkingDirectory;

    public async Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default)
    {
        var c = await ClientAsync(ct);
        if (path.Length == 0) path = c.WorkingDirectory;
        var list = new List<FsItem>();
        await foreach (var f in c.ListDirectoryAsync(path, ct))
        {
            if (f.Name is "." or "..") continue;
            list.Add(Item(f));
        }
        return list;
    }

    private static FsItem Item(ISftpFile f) => new()
    {
        Name = f.Name, FullPath = f.FullName, IsDir = f.IsDirectory, IsLink = f.IsSymbolicLink, Length = f.Length,
        Modified = f.LastWriteTime, Perms = PermString(f.Attributes), Owner = $"{f.UserId}:{f.GroupId}", Mode = ModeBits(f.Attributes),
    };

    public static int ModeBits(SftpFileAttributes a) =>
        (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0) |
        (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0) |
        (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    public static string PermString(SftpFileAttributes a)
    {
        var sb = new StringBuilder(a.IsDirectory ? "d" : a.IsSymbolicLink ? "l" : "-");
        sb.Append(a.OwnerCanRead ? 'r' : '-').Append(a.OwnerCanWrite ? 'w' : '-').Append(a.OwnerCanExecute ? 'x' : '-');
        sb.Append(a.GroupCanRead ? 'r' : '-').Append(a.GroupCanWrite ? 'w' : '-').Append(a.GroupCanExecute ? 'x' : '-');
        sb.Append(a.OthersCanRead ? 'r' : '-').Append(a.OthersCanWrite ? 'w' : '-').Append(a.OthersCanExecute ? 'x' : '-');
        return sb.ToString();
    }

    public async Task<FsItem?> StatAsync(string path, CancellationToken ct = default)
    {
        var c = await ClientAsync(ct);
        return await Task.Run(() =>
        {
            if (!c.Exists(path)) return null;
            var a = c.GetAttributes(path);
            var name = path.TrimEnd('/'); name = name[(name.LastIndexOf('/') + 1)..];
            return new FsItem { Name = name, FullPath = path, IsDir = a.IsDirectory, IsLink = a.IsSymbolicLink, Length = a.Size, Modified = a.LastWriteTime, Perms = PermString(a), Owner = $"{a.UserId}:{a.GroupId}", Mode = ModeBits(a) };
        }, ct);
    }

    public async Task MkdirAsync(string path, CancellationToken ct = default) => await (await ClientAsync(ct)).CreateDirectoryAsync(path, ct);

    public async Task DeleteAsync(string path, bool isDir, CancellationToken ct = default)
    {
        var c = await ClientAsync(ct);
        if (!isDir) { await c.DeleteFileAsync(path, ct); return; }
        // Recursive over SFTP itself (works on SFTP-only accounts too). Symlinks are removed, never followed.
        async Task Rm(string dir)
        {
            await foreach (var f in c.ListDirectoryAsync(dir, ct))
            {
                if (f.Name is "." or "..") continue;
                if (f.IsDirectory && !f.IsSymbolicLink) await Rm(f.FullName);
                else await c.DeleteFileAsync(f.FullName, ct);
            }
            await c.DeleteDirectoryAsync(dir, ct);
        }
        await Rm(path);
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct = default) => await (await ClientAsync(ct)).RenameFileAsync(from, to, ct);

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => await (await ClientAsync(ct)).OpenAsync(path, FileMode.Open, FileAccess.Read, ct);

    public async Task<Stream> CreateAsync(string path, CancellationToken ct = default) => await (await ClientAsync(ct)).OpenAsync(path, FileMode.Create, FileAccess.Write, ct);

    public bool CanAppend => true;

    public async Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) => await (await ClientAsync(ct)).OpenAsync(path, FileMode.Append, FileAccess.Write, ct);

    public async Task ReplaceAsync(string from, string to, CancellationToken ct = default)
    {
        var c = await ClientAsync(ct);
        await Task.Run(() =>
        {
            // posix-rename@openssh.com replaces atomically; plain SFTP v3 rename refuses an existing target.
            try { c.RenameFile(from, to, isPosix: true); }
            catch (Exception) { if (c.Exists(to)) c.DeleteFile(to); c.RenameFile(from, to); }
        }, ct);
    }

    public async Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default)
    {
        var c = await ClientAsync(ct);
        await Task.Run(() => { var a = c.GetAttributes(path); a.LastWriteTime = modified; a.LastAccessTime = DateTime.Now; c.SetAttributes(path, a); }, ct);
    }

    public async Task SetModeAsync(string path, int mode, CancellationToken ct = default)
    {
        if (mode <= 0) return;
        var c = await ClientAsync(ct);
        await Task.Run(() => c.ChangePermissions(path, (short)(mode & 0xFFF)), ct);
    }

    /// <summary>sha256sum on the server (one SSH exec instead of reading the file back); null when unavailable.</summary>
    public async Task<string?> HashAsync(string path, CancellationToken ct = default) => await ScpFs.RemoteSha256(_host, path, ct);

    public string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public string Parent(string p) => PosixParent(p);

    public static string PosixParent(string p)
    {
        if (p is "" or "/") return "/";
        var t = p.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    public void Dispose() { _session?.Dispose(); _session = null; }
}
