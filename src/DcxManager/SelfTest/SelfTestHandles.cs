using System.Runtime.InteropServices;

namespace DcxManager;

/// <summary>
/// Self-test helper: counts this process's kernel handles by object type (Thread, Event, File, Key, Section, …) so an
/// endurance failure names what grew instead of only a total (ADR-025). Reads the system handle table
/// (SystemExtendedHandleInformation) filtered to our PID and asks NtQueryObject for each type's name once.
/// Only type information is queried — never names, which can hang on synchronous pipes.
/// </summary>
internal static class HandleCensus
{
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    private static readonly Dictionary<ushort, string> TypeNames = new();

    public static Dictionary<string, int> Take()
    {
        var counts = new Dictionary<string, int>();
        int len = 1 << 22;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                buf = Marshal.AllocHGlobal(len);
                int st = NtQuerySystemInformation(64, buf, len, out int ret);
                if (st == 0) break;
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (st != unchecked((int)0xC0000004)) return counts;   // anything but STATUS_INFO_LENGTH_MISMATCH
                len = Math.Max(len * 2, ret + (1 << 16));
            }
            if (buf == IntPtr.Zero) return counts;
            long n = Marshal.ReadIntPtr(buf).ToInt64();
            long pid = Environment.ProcessId;
            const int entry = 40, header = 16;
            for (long i = 0; i < n; i++)
            {
                var e = buf + (int)(header + i * entry);
                if (Marshal.ReadIntPtr(e, 8).ToInt64() != pid) continue;
                var handle = Marshal.ReadIntPtr(e, 16);
                ushort typeIndex = (ushort)Marshal.ReadInt16(e, 26);
                var name = TypeName(typeIndex, handle);
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
            }
        }
        catch { }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return counts;
    }

    private static string TypeName(ushort index, IntPtr handle)
    {
        if (TypeNames.TryGetValue(index, out var known)) return known;
        var b = Marshal.AllocHGlobal(4096);
        try
        {
            if (NtQueryObject(handle, 2, b, 4096, out _) != 0) return "type#" + index;
            int bytes = (ushort)Marshal.ReadInt16(b);
            var s = Marshal.PtrToStringUni(Marshal.ReadIntPtr(b, 8), bytes / 2);
            TypeNames[index] = s;
            return s;
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr h, int cls, out IntPtr info, int len, IntPtr ret);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(int access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetModuleHandleEx(int flags, IntPtr addr, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetModuleFileName(IntPtr module, System.Text.StringBuilder s, int n);

    /// <summary>This process's threads grouped by the module their start address is in (managed pool threads start in
    /// coreclr, native thread-pool / RPC / COM workers in ntdll).</summary>
    public static Dictionary<string, int> Threads()
    {
        var counts = new Dictionary<string, int>();
        foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
        {
            string mod = "?";
            var h = OpenThread(0x0040 /* QUERY_INFORMATION */, false, t.Id);
            if (h != IntPtr.Zero)
            {
                if (NtQueryInformationThread(h, 9, out var start, IntPtr.Size, IntPtr.Zero) == 0
                    && GetModuleHandleEx(6 /* FROM_ADDRESS | UNCHANGED_REFCOUNT */, start, out var m))
                {
                    var sb = new System.Text.StringBuilder(260);
                    GetModuleFileName(m, sb, 260);
                    mod = System.IO.Path.GetFileName(sb.ToString()) + "+" + (start.ToInt64() - m.ToInt64()).ToString("x");
                }
                CloseHandle(h);
            }
            counts[mod] = counts.TryGetValue(mod, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    /// <summary>"Event +31, File +4, …" — the types that changed, largest growth first.</summary>
    public static string Diff(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var parts = after.Keys.Union(before.Keys)
            .Select(k => (k, d: after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
            .Where(x => x.d != 0).OrderByDescending(x => Math.Abs(x.d))
            .Select(x => $"{x.k} {x.d:+0;-0}");
        var s = string.Join(", ", parts);
        return s.Length == 0 ? "no change by type" : s;
    }
}
