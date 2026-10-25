using DcxManager.Services;

namespace DcxManager.Files;

/// <summary>How two copies of a file are judged the same.</summary>
public enum CompareBy { TimeAndSize, Content, Size }

/// <summary>Two-way remembers the last sync; Mirror and Update go one way; Custom sets the action per kind of difference.</summary>
public enum SyncVariant { TwoWay, Mirror, Update, Custom }

/// <summary>What happens when both sides changed (Two-way), or when they differ without a last-sync record.</summary>
public enum ConflictPolicy { Skip, NewerWins, LeftWins, RightWins }

/// <summary>Where deleted and overwritten files go. The Recycle Bin is this PC's; on a server it falls back to versioning.</summary>
public enum DeletionMode { RecycleBin, Versioning, Permanent }

/// <summary>Versions: "name 2026-10-25 172233.ext" next to each other, one folder per sync run, or only the last one.</summary>
public enum VersionNaming { TimestampFile, TimestampFolder, Replace }

public enum SyncSchedule { Off, Interval, Daily, Weekly, RealTime }

/// <summary>Every switch of a sync (0.6.26, ADR-102). A job has one set; a folder pair can override it.</summary>
public sealed class SyncOptions
{
    // ---- compare ----
    public CompareBy Compare { get; set; } = CompareBy.TimeAndSize;
    /// <summary>Times this close count as equal (FAT and SMB keep 2-second times).</summary>
    public int ToleranceSeconds { get; set; } = 2;
    /// <summary>Whole-hour offsets that also count as equal (daylight saving, FAT drives): "1" or "1,2"; empty = none.</summary>
    public string IgnoreHourShifts { get; set; } = "";

    // ---- synchronise ----
    public SyncVariant Variant { get; set; } = SyncVariant.TwoWay;
    /// <summary>Mirror and Update: left → right (true) or right → left.</summary>
    public bool LeftToRight { get; set; } = true;
    /// <summary>Custom: the action for each kind of difference (missing kinds do nothing).</summary>
    public Dictionary<DiffState, SyncAction> Custom { get; set; } = new()
    {
        [DiffState.OnlyLeft] = SyncAction.CopyRight, [DiffState.OnlyRight] = SyncAction.CopyLeft,
        [DiffState.LeftNewer] = SyncAction.CopyRight, [DiffState.RightNewer] = SyncAction.CopyLeft,
        [DiffState.Differs] = SyncAction.Conflict, [DiffState.TypeConflict] = SyncAction.Conflict,
    };
    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.Skip;
    /// <summary>Mirror: delete what isn't in the source (FreeFileSync always does; here it can be switched off).</summary>
    public bool MirrorDeletes { get; set; } = true;

    // ---- deletions and versions ----
    public DeletionMode Deletion { get; set; } = DeletionMode.RecycleBin;
    public VersionNaming Naming { get; set; } = VersionNaming.TimestampFile;
    /// <summary>Versions folder on the left / right (an absolute path on that side); empty = "&lt;sync folder&gt;/.dcx-versions".</summary>
    public string VersionsLeft { get; set; } = "";
    public string VersionsRight { get; set; } = "";
    /// <summary>Versions older than this are removed after a sync (0 = keep forever).</summary>
    public int VersionsKeepDays { get; set; } = 30;
    /// <summary>At most this many versions of one file (0 = no limit).</summary>
    public int VersionsMaxCount { get; set; }

    // ---- safety ----
    /// <summary>A run without a preview (schedule, real-time, Run now) stops when it would delete more than this share of a
    /// side's files (and more than 10): an unmounted or emptied folder must not wipe the other side.</summary>
    public int MaxDeletePercent { get; set; } = 50;
    /// <summary>Copies are checked with SHA-256 (null = Settings → transfers).</summary>
    public bool? Verify { get; set; }

    public SyncFilterDef Filter { get; set; } = new();

    public SyncOptions Clone()
    {
        var o = (SyncOptions)MemberwiseClone();
        o.Custom = new(Custom);
        o.Filter = Filter.Clone();
        return o;
    }

    /// <summary>New jobs start from Settings → Folder sync.</summary>
    public static SyncOptions Defaults()
    {
        var s = AppSettings.Current;
        return new SyncOptions
        {
            Deletion = Enum.TryParse<DeletionMode>(s.FileSyncDeletion, true, out var d) ? d : DeletionMode.RecycleBin,
            VersionsKeepDays = Math.Clamp(s.FileSyncVersionsKeepDays, 0, 3650),
            MaxDeletePercent = Math.Clamp(s.FileSyncMaxDeletePercent, 1, 100),
        };
    }

    public string Describe() => Variant switch
    {
        SyncVariant.TwoWay => "Two-way",
        SyncVariant.Mirror => LeftToRight ? "Mirror →" : "Mirror ←",
        SyncVariant.Update => LeftToRight ? "Update →" : "Update ←",
        _ => "Custom",
    } + Compare switch { CompareBy.Content => " · by content", CompareBy.Size => " · by size", _ => "" };
}

/// <summary>One pair of folders: each side is this PC or a saved host ("local" / "host:&lt;id&gt;", as <see cref="FsRef"/>).</summary>
public sealed class SyncPairDef
{
    public string Left { get; set; } = "local";
    public string LeftPath { get; set; } = "";
    public string Right { get; set; } = "local";
    public string RightPath { get; set; } = "";
    /// <summary>Settings for this pair only (null = the job's).</summary>
    public SyncOptions? Override { get; set; }

    public FsRef LeftRef => FsRef.Parse(Left);
    public FsRef RightRef => FsRef.Parse(Right);
    public string Text => $"{LeftRef.Title}:{LeftPath}  ⇄  {RightRef.Title}:{RightPath}";
    public SyncPairDef Clone() => new() { Left = Left, LeftPath = LeftPath, Right = Right, RightPath = RightPath, Override = Override?.Clone() };
}

/// <summary>A saved sync job (like a FreeFileSync configuration): folder pairs, options and an optional schedule.</summary>
public sealed class SyncJobDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New sync";
    public List<SyncPairDef> Pairs { get; set; } = new();
    public SyncOptions Options { get; set; } = new();

    public SyncSchedule Schedule { get; set; } = SyncSchedule.Off;
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Daily / weekly time, "HH:mm" local.</summary>
    public string At { get; set; } = "02:00";
    /// <summary>Weekly: bit 0 = Sunday … bit 6 = Saturday.</summary>
    public int Days { get; set; } = 0b0111110;
    /// <summary>Real-time: seconds of quiet after the last change on this PC before it runs.</summary>
    public int RealTimeDelaySeconds { get; set; } = 10;
    /// <summary>Real-time: a server can't be watched, so its side is checked this often (minutes).</summary>
    public int RemotePollMinutes { get; set; } = 15;

    public DateTime? LastRunUtc { get; set; }
    public string LastResult { get; set; } = "";

    public SyncOptions OptionsFor(SyncPairDef p) => p.Override ?? Options;

    public SyncJobDef Clone() => new()
    {
        Id = Id, Name = Name, Pairs = Pairs.Select(p => p.Clone()).ToList(), Options = Options.Clone(), Schedule = Schedule, IntervalMinutes = IntervalMinutes,
        At = At, Days = Days, RealTimeDelaySeconds = RealTimeDelaySeconds, RemotePollMinutes = RemotePollMinutes, LastRunUtc = LastRunUtc, LastResult = LastResult,
    };

    public string ScheduleText => Schedule switch
    {
        SyncSchedule.Interval => IntervalMinutes % 60 == 0 ? $"every {IntervalMinutes / 60} h" : $"every {IntervalMinutes} min",
        SyncSchedule.Daily => $"daily at {At}",
        SyncSchedule.Weekly => $"{string.Join(" ", Enumerable.Range(0, 7).Where(d => (Days & (1 << d)) != 0).Select(d => ((DayOfWeek)d).ToString()[..3]))} at {At}",
        SyncSchedule.RealTime => $"real-time ({RealTimeDelaySeconds} s after a change)",
        _ => "manual",
    };

    /// <summary>The next run (UTC) for a timed schedule, or null (manual / real-time).</summary>
    public DateTime? NextRunUtc(DateTime nowUtc)
    {
        var last = LastRunUtc;
        switch (Schedule)
        {
            case SyncSchedule.Interval:
                return last == null ? nowUtc : last.Value.AddMinutes(Math.Clamp(IntervalMinutes, 1, 60 * 24 * 31));
            case SyncSchedule.Daily or SyncSchedule.Weekly:
            {
                if (!TimeSpan.TryParse(At, System.Globalization.CultureInfo.InvariantCulture, out var at) || at < TimeSpan.Zero || at >= TimeSpan.FromDays(1)) at = TimeSpan.FromHours(2);
                int days = Schedule == SyncSchedule.Daily ? 0x7F : Days & 0x7F;
                if (days == 0) return null;
                // The first slot after the last run (or after "now" for a job that never ran: it waits for its time).
                var from = (last ?? nowUtc).ToLocalTime();
                for (int i = 0; i <= 7; i++)
                {
                    var slot = from.Date.AddDays(i) + at;
                    if (slot > from && (days & (1 << (int)slot.DayOfWeek)) != 0) return slot.ToUniversalTime();
                }
                return null;
            }
            default: return null;
        }
    }
}
