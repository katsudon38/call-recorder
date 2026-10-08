using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;

namespace CallRecorder;

public sealed class Settings
{
    public string? RecordingDirectory { get; set; }
    public string? CaptureDeviceId { get; set; }
    public string? RenderDeviceId { get; set; }
    public string DefaultDeviceRole { get; set; } = "Communications";
    public double SegmentMinutes { get; set; } = 30;
    public long MaxSegmentDataBytes { get; set; } = 1073741824;
    public double HeaderFlushSeconds { get; set; } = 2;
    public double DurableFlushSeconds { get; set; } = 10;
    public double QueueCapacitySeconds { get; set; } = 30;
    public double QueueWarningSeconds { get; set; } = 15;
    public long MinimumFreeBytes { get; set; } = 2147483648;
    public string DeviceConfiguration { get; set; } = "未分類";
    public string DirectoryPath => Path.GetFullPath(RecordingDirectory ?? Paths.Recordings);
    public void Validate()
    {
        if (DefaultDeviceRole != "Communications") throw new ArgumentException("defaultDeviceRole は Communications のみ対応します。");
        if (!double.IsFinite(SegmentMinutes) || SegmentMinutes <= 0 || SegmentMinutes > 30 ||
            MaxSegmentDataBytes < 1024 || MaxSegmentDataBytes > 1073741824 ||
            !double.IsFinite(HeaderFlushSeconds) || HeaderFlushSeconds <= 0 || HeaderFlushSeconds > 2 ||
            !double.IsFinite(DurableFlushSeconds) || DurableFlushSeconds <= 0 || DurableFlushSeconds > 10 ||
            !double.IsFinite(QueueCapacitySeconds) || QueueCapacitySeconds <= 0 || QueueCapacitySeconds > 30 ||
            !double.IsFinite(QueueWarningSeconds) || QueueWarningSeconds <= 0 || QueueWarningSeconds >= QueueCapacitySeconds ||
            MinimumFreeBytes < 2147483648) throw new ArgumentException("設定の範囲が不正です。settings.example.json を確認してください。");
        _ = DirectoryPath;
    }
    public static Settings Load()
    {
        Directory.CreateDirectory(Paths.Root);
        if (!File.Exists(Paths.Settings)) AtomicJson.Write(Paths.Settings, new Settings());
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings), AtomicJson.Options)
                     ?? throw new InvalidDataException("設定が空です。");
        result.Validate(); return result;
    }
}
public static class Paths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallRecorder");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Recordings => Path.Combine(Root, "Recordings");
    public static string Logs => Path.Combine(Root, "Logs");
}
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static void Write(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}
public static class Events
{
    private static readonly object Gate = new();
    public static void Log(string kind, object? detail = null)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(Path.Combine(Paths.Logs, "events.jsonl"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, detail }) + Environment.NewLine);
        }
    }
}
public sealed class TrackInfo
{
    public string Source { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Format { get; set; } = "";
    public int SampleRate { get; set; }
    public int BlockAlign { get; set; }
    public long StartBeforeTicks { get; set; }
    public long StartAfterTicks { get; set; }
    public long? FirstCallbackTicks { get; set; }
    public long LastCallbackTicks { get; set; }
    public long CapturedFrames { get; set; }
    public long LeadingPaddingFrames { get; set; }
    public long TrailingPaddingFrames { get; set; }
    public long MaximumQueuedBytes { get; set; }
}
public sealed class SegmentInfo
{
    public string Source { get; set; } = "";
    public int Number { get; set; }
    public string FileName { get; set; } = "";
    public long DataBytes { get; set; }
    public long Frames { get; set; }
    public double TimelineStartSeconds { get; set; }
    public double TimelineEndSeconds { get; set; }
    public string Status { get; set; } = "recording";
}
public sealed class SessionInfo
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string DesignVersion { get; set; } = "0.3";
    public string AppVersion { get; set; } = "0.1.0";
    public string NAudioVersion { get; set; } = "2.2.1";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;
    public string ConsentResponse { get; set; } = "Y";
    public string ConsentPromptVersion { get; set; } = "v0.3-1";
    public DateTimeOffset ConsentUtc { get; set; }
    public string DeviceConfiguration { get; set; } = "未分類";
    public long ReferenceTicks { get; set; }
    public long StopwatchFrequency { get; set; } = Stopwatch.Frequency;
    public string Status { get; set; } = "starting";
    public string? StopReason { get; set; }
    public List<string> Errors { get; set; } = [];
    public TrackInfo[] Tracks { get; set; } = [];
    public List<SegmentInfo> Segments { get; set; } = [];
    public double SegmentSeconds { get; set; }
    public double MaximumFlushMilliseconds { get; set; }
    public double MaximumWriteMilliseconds { get; set; }
    public double? StopMilliseconds { get; set; }
    public string TimingNote { get; set; } = "開始呼出時刻に基づく先頭補填。受信時刻はサンプル取得時刻ではない。途中欠落・機器クロックのドリフト補正なし。";
}
