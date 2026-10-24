using System.Globalization;
using System.IO;
using DcxManager.Services;
using DcxManager.Ssh;
using Renci.SshNet;

namespace DcxManager.Files;

/// <summary>
/// SCP fallback for servers without the SFTP subsystem (ADR-014): listings and file operations through the shell
/// (`stat -c`, mkdir, rm, mv, chmod, touch — GNU and BusyBox; every path single-quoted), data through SSH.NET's
/// ScpClient via a temporary file in the app's private temp folder (deleted on close). No append: resume restarts a file.
/// </summary>
public sealed class ScpFs : IFileSystem
{
    private readonly HostEntry _host;
    private SshSession<ScpClient>? _scp;
    private readonly SemaphoreSlim _connect = new(1, 1);

    public ScpFs(HostEntry host) => _host = host;
    public FsRef Ref => FsRef.ForHost(_host);
    public string Title => _host.Display + " · SCP";
    public bool IsRemote => true;
    public bool CanAppend => false;
    public HostEntry Host => _host;

    private static string Q(string s) => RemoteExec.Quote(s);

    private async Task<ExecResult> Sh(string cmd, CancellationToken ct) => await SshPool.RunAsync(_host, cmd, ct: ct);

    private async Task<ExecResult> Must(string cmd, CancellationToken ct)
    {
        var r = await Sh(cmd, ct);
        if (!r.Ok) throw new IOException((r.Error.Trim().Length > 0 ? r.Error.Trim() : r.Output.Trim()).Split('\n')[0]);
        return r;
    }

    private async Task<ScpClient> ScpAsync(CancellationToken ct)
    {
        if (_scp is { IsConnected: true }) return _scp.Client;
        await _connect.WaitAsync(ct);
        try
        {
            if (_scp is { IsConnected: true }) return _scp.Client;
            _scp?.Dispose();
            _scp = await SshConnector.ConnectScpAsync(_host, ct);
            return _scp.Client;
        }
        finally { _connect.Release(); }
    }

    public async Task<string> HomeAsync(CancellationToken ct = default) => (await Must("pwd", ct)).Output.Trim();

    /// <summary>One `stat -c '%F|%s|%Y|%a|%u|%g|%n'` line → item (the name is the rest of the line, so '|' in names is fine).</summary>
    public static FsItem? ParseStat(string line, string dir)
    {
        var p = line.Split('|', 7);
        if (p.Length < 7 || !long.TryParse(p[1], out var size) || !long.TryParse(p[2], out var epoch)) return null;
        var name = p[6];
        if (name.Length == 0) return null;
        bool isDir = p[0] == "directory", isLink = p[0] == "symbolic link";
        // Octal digits only (a fuzzed "89" made Convert.ToInt32(.., 8) throw).
        int mode = p[3].Length is > 0 and <= 6 && p[3].All(c => c is >= '0' and <= '7') ? Convert.ToInt32(p[3], 8) : 0;
        var full = name.StartsWith('/') ? name : (dir.EndsWith('/') ? dir + name : dir + "/" + name);
        var short_ = full.TrimEnd('/'); short_ = short_[(short_.LastIndexOf('/') + 1)..];
        return new FsItem
        {
            Name = short_, FullPath = full, IsDir = isDir, IsLink = isLink, Length = isDir ? 0 : size,
            Modified = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime, Mode = mode, Perms = Perm(isDir, isLink, mode), Owner = $"{p[4]}:{p[5]}",
        };
    }

    private static string Perm(bool dir, bool link, int m)
    {
        var s = new char[10];
        s[0] = dir ? 'd' : link ? 'l' : '-';
        const string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++) s[i + 1] = (m & (0x100 >> i)) != 0 ? rwx[i] : '-';
        return new string(s);
    }

    public async Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default)
    {
        if (path.Length == 0) path = await HomeAsync(ct);
        var r = await Must($"cd {Q(path)} && for f in .[!.]* ..?* *; do {{ [ -e \"$f\" ] || [ -L \"$f\" ]; }} || continue; stat -c '%F|%s|%Y|%a|%u|%g|%n' -- \"$f\"; done", ct);
        return r.Output.Split('\n').Select(l => ParseStat(l.TrimEnd('\r'), path)).OfType<FsItem>().ToList();
    }

    public async Task<FsItem?> StatAsync(string path, CancellationToken ct = default)
    {
        var r = await Sh($"stat -c '%F|%s|%Y|%a|%u|%g|%n' -- {Q(path)}", ct);
        if (!r.Ok) return null;
        var it = ParseStat(r.Output.Trim(), SftpFs.PosixParent(path));
        return it == null ? null : new FsItem { Name = it.Name, FullPath = path, IsDir = it.IsDir, IsLink = it.IsLink, Length = it.Length, Modified = it.Modified, Mode = it.Mode, Perms = it.Perms, Owner = it.Owner };
    }

    public async Task MkdirAsync(string path, CancellationToken ct = default) => await Must($"mkdir -- {Q(path)}", ct);
    public async Task DeleteAsync(string path, bool isDir, CancellationToken ct = default) => await Must(isDir ? $"rm -rf -- {Q(path)}" : $"rm -f -- {Q(path)}", ct);
    public async Task RenameAsync(string from, string to, CancellationToken ct = default) => await Must($"mv -- {Q(from)} {Q(to)}", ct);
    public async Task ReplaceAsync(string from, string to, CancellationToken ct = default) => await Must($"mv -f -- {Q(from)} {Q(to)}", ct);
    public async Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default) =>
        await Must($"TZ=UTC touch -m -d {Q(modified.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))} -- {Q(path)}", ct);
    public async Task SetModeAsync(string path, int mode, CancellationToken ct = default) { if (mode > 0) await Must($"chmod {Convert.ToString(mode & 0xFFF, 8)} -- {Q(path)}", ct); }
    public Task<string?> HashAsync(string path, CancellationToken ct = default) => RemoteSha256(_host, path, ct);

    /// <summary>sha256sum (GNU/BusyBox) on the server; null when the command isn't there.</summary>
    internal static async Task<string?> RemoteSha256(HostEntry host, string path, CancellationToken ct)
    {
        try
        {
            var r = await SshPool.RunAsync(host, $"sha256sum -- {Q(path)}", ct: ct, timeout: TimeSpan.FromMinutes(30));
            var h = r.Ok ? r.Output.Trim().Split(' ', 2)[0] : "";
            return h.Length == 64 && h.All(Uri.IsHexDigit) ? h.ToUpperInvariant() : null;
        }
        catch (Exception ex) { Log.Write("Remote sha256sum", ex); return null; }
    }

    private static string TempFile()
    {
        var dir = Path.Combine(AppInfo.CacheDir, "transfer-tmp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N"));
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var scp = await ScpAsync(ct);
        var tmp = TempFile();
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) await Task.Run(() => scp.Download(path, fs), ct);
            return new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    public async Task<Stream> CreateAsync(string path, CancellationToken ct = default) => new UploadStream(await ScpAsync(ct), path, TempFile());

    public Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) => throw new NotSupportedException("SCP can't append; the file restarts.");

    public string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;
    public string Parent(string p) => SftpFs.PosixParent(p);
    public void Dispose() { _scp?.Dispose(); _scp = null; }

    /// <summary>Buffers to a private temp file and uploads it when closed (an upload failure surfaces at close).</summary>
    private sealed class UploadStream : Stream
    {
        private readonly ScpClient _scp;
        private readonly string _remote, _tmp;
        private readonly FileStream _inner;
        private bool _closed;
        public UploadStream(ScpClient scp, string remote, string tmp) { _scp = scp; _remote = remote; _tmp = tmp; _inner = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => _inner.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => _inner.WriteAsync(b, ct);

        private void Upload()
        {
            if (_closed) return;
            _closed = true;
            try { _inner.Position = 0; _scp.Upload(_inner, _remote); }
            finally { _inner.Dispose(); try { File.Delete(_tmp); } catch { } }
        }

        protected override void Dispose(bool disposing) { if (disposing) Upload(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await Task.Run(Upload); await base.DisposeAsync(); }
    }
}

/// <summary>
/// A saved host's files: SFTP, or SCP when the host is set to SCP or (auto) when the server refuses the SFTP subsystem.
/// The switch happens once, on the first operation that hits it; the pane title then says "· SCP".
/// </summary>
public sealed class HostFs : IFileSystem
{
    private readonly HostEntry _host;
    private IFileSystem _inner;
    public HostFs(HostEntry host) { _host = host; _inner = host.FileProtocol == "scp" ? new ScpFs(host) : new SftpFs(host); }
    public HostEntry Host => _host;
    public bool UsingScp => _inner is ScpFs;
    public FsRef Ref => FsRef.ForHost(_host);
    public string Title => _inner.Title;
    public bool IsRemote => true;
    public bool CanAppend => _inner.CanAppend;

    /// <summary>"Subsystem 'sftp' could not be executed" and friends.</summary>
    public static bool NoSftp(Exception ex) =>
        ex.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase) || ex.InnerException?.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<T> Do<T>(Func<IFileSystem, Task<T>> op)
    {
        try { return await op(_inner); }
        catch (Exception ex) when (_inner is SftpFs && _host.FileProtocol == "auto" && NoSftp(ex))
        {
            _inner.Dispose();
            _inner = new ScpFs(_host);
            AuditLog.Write("app", _host.Display, "SFTP not available on the server — using SCP");
            return await op(_inner);
        }
    }
    private Task Do(Func<IFileSystem, Task> op) => Do(async fs => { await op(fs); return 0; });

    public Task<string> HomeAsync(CancellationToken ct = default) => Do(fs => fs.HomeAsync(ct));
    public Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default) => Do(fs => fs.ListAsync(path, ct));
    public Task<FsItem?> StatAsync(string path, CancellationToken ct = default) => Do(fs => fs.StatAsync(path, ct));
    public Task MkdirAsync(string path, CancellationToken ct = default) => Do(fs => fs.MkdirAsync(path, ct));
    public Task DeleteAsync(string path, bool isDir, CancellationToken ct = default) => Do(fs => fs.DeleteAsync(path, isDir, ct));
    public Task RenameAsync(string from, string to, CancellationToken ct = default) => Do(fs => fs.RenameAsync(from, to, ct));
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Do(fs => fs.OpenReadAsync(path, ct));
    public Task<Stream> CreateAsync(string path, CancellationToken ct = default) => Do(fs => fs.CreateAsync(path, ct));
    public Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) => Do(fs => fs.OpenAppendAsync(path, ct));
    public Task ReplaceAsync(string from, string to, CancellationToken ct = default) => Do(fs => fs.ReplaceAsync(from, to, ct));
    public Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default) => Do(fs => fs.SetTimesAsync(path, modified, ct));
    public Task SetModeAsync(string path, int mode, CancellationToken ct = default) => Do(fs => fs.SetModeAsync(path, mode, ct));
    public Task<string?> HashAsync(string path, CancellationToken ct = default) => Do(fs => fs.HashAsync(path, ct));
    public string Join(string dir, string name) => _inner.Join(dir, name);
    public string Parent(string path) => _inner.Parent(path);
    public void Dispose() => _inner.Dispose();
}
