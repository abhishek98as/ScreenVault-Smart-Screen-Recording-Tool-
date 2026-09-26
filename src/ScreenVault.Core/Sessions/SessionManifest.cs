using System.Text.Json.Serialization;

namespace ScreenVault.Core.Sessions;

public sealed class SessionVideoMeta
{
    public string EncoderProfile { get; set; } = string.Empty;
    public int Fps { get; set; } = 15;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
}

public sealed class SegmentManifestEntry
{
    public int Index { get; set; }
    public string Location { get; set; } = string.Empty;
    public string TsPath { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public long Bytes { get; set; }
    public string OpenReason { get; set; } = string.Empty;
    public string? CloseReason { get; set; }
    public string Remux { get; set; } = "Pending";
    public double? DurationSec { get; set; }
}

public sealed class MarkerEntry
{
    public DateTime AtUtc { get; set; }
    public double OffsetSec { get; set; }
    public string Note { get; set; } = string.Empty;
    public string Kind { get; set; } = "User"; // User or System
}

public sealed class SessionEventEntry
{
    public DateTime AtUtc { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class SessionManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string SessionId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Status { get; set; } = "Recording"; // Recording | Completed | Interrupted
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string Machine { get; set; } = Environment.MachineName;
    public string AppVersion { get; set; } = "1.1.0";
    public SessionVideoMeta Video { get; set; } = new();
    public bool Protected { get; set; }
    public string? MergedPath { get; set; }
    public string MergeStatus { get; set; } = "NotRequested"; // NotRequested | Pending | Done | Failed
    public List<SegmentManifestEntry> Segments { get; set; } = [];
    public List<MarkerEntry> Markers { get; set; } = [];
    public List<SessionEventEntry> Events { get; set; } = [];
}
