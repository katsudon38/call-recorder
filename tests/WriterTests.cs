using CallRecorder;
using NAudio.Wave;
using Xunit;
using System.Diagnostics;
using System.Text.Json;

public sealed class WriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "callrecorder-tests-" + Guid.NewGuid());
    public WriterTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static SessionInfo Session() => new() { ReferenceTicks = Stopwatch.GetTimestamp(), Tracks = [new() { Source = "mic", SampleRate = 8000, BlockAlign = 2 }, new() { Source = "speaker", SampleRate = 8000, BlockAlign = 2 }] };
    private static byte[] Signal(int frames)
    {
        var bytes = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BitConverter.GetBytes((short)(i % 32000 - 16000)).CopyTo(bytes, i * 2);
        return bytes;
    }
    private byte[] ReadTrack(string source) => Directory.GetFiles(root, $"*_{source}.wav").Order().SelectMany(path =>
    { using var reader = new WaveFileReader(path); var bytes = new byte[checked((int)reader.Length)]; Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length)); return bytes; }).ToArray();
    [Fact]
    public async Task BlocksAcrossTimeBoundariesPreserveEverySample()
    {
        var session = Session();
        var settings = new Settings { SegmentMinutes = 0.01 };
        var errors = new List<Exception>();
        var writer = new SegmentWriter(root, settings, session, [new(8000, 16, 1), new(8000, 16, 1)], errors.Add);
        var bytes = Signal(16000); writer.Offer(0, bytes, bytes.Length); writer.Offer(1, bytes, bytes.Length);
        writer.Start(); await writer.FinishAsync(true);
        Assert.Empty(errors); Assert.Equal(bytes, ReadTrack("mic")); Assert.Equal(bytes, ReadTrack("speaker"));
        Assert.Equal(8, session.Segments.Count); Assert.All(session.Segments, s => Assert.True(s.DataBytes <= 9600));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "session.json")));
        Assert.Equal(8, metadata.RootElement.GetProperty("segments").GetArrayLength());
    }
    [Fact]
    public async Task CapacityBoundaryAppliesToBothFormats()
    {
        var session = Session(); session.Tracks[1].SampleRate = 16000; session.Tracks[1].BlockAlign = 4;
        var writer = new SegmentWriter(root, new Settings { MaxSegmentDataBytes = 1024 }, session, [new(8000, 16, 1), new(16000, 16, 2)], e => throw e);
        var mic = Signal(1600); var speaker = Signal(6400);
        writer.Offer(0, mic, mic.Length); writer.Offer(1, speaker, speaker.Length); writer.Start(); await writer.FinishAsync(true);
        Assert.Equal(mic, ReadTrack("mic")); Assert.Equal(speaker, ReadTrack("speaker"));
        Assert.All(session.Segments, s => Assert.True(s.DataBytes <= 1024));
        Assert.Equal(session.Segments.Where(s => s.Source == "mic").Select(s => s.Number), session.Segments.Where(s => s.Source == "speaker").Select(s => s.Number));
    }
    [Fact]
    public async Task QueueOverflowReportsFailureAndRetainsPartialFiles()
    {
        Exception? fault = null; var session = Session();
        var writer = new SegmentWriter(root, new Settings { QueueCapacitySeconds = 0.1 }, session, [new(8000, 16, 1), new(8000, 16, 1)], e => fault = e);
        var bytes = Signal(400); Assert.True(writer.Offer(0, bytes, bytes.Length));
        Assert.False(writer.Offer(0, Signal(1000), 2000)); Assert.IsType<IOException>(fault);
        writer.Start(); await writer.FinishAsync(false);
        Assert.Equal(2, Directory.GetFiles(root, "*.partial").Length); Assert.Empty(Directory.GetFiles(root, "*.wav"));
        Assert.All(session.Segments, s => Assert.Equal("recoverable", s.Status));
    }
    [Fact]
    public async Task PeriodicFlushMakesInProgressDataReadable()
    {
        var session = Session();
        var writer = new SegmentWriter(root, new Settings { HeaderFlushSeconds = 0.01, DurableFlushSeconds = 0.02 }, session, [new(8000, 16, 1), new(8000, 16, 1)], e => throw e);
        var bytes = Signal(800); writer.Offer(0, bytes, bytes.Length); writer.Offer(1, bytes, bytes.Length); writer.Start();
        long observedLength = -1;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    using var stream = new FileStream(Path.Combine(root, "000001_mic.wav.partial"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new WaveFileReader(stream);
                    observedLength = reader.Length;
                    if (observedLength == bytes.Length) break;
                }
                catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) { }
                await Task.Delay(50);
            }
            Assert.Equal(bytes.Length, observedLength);
        }
        finally { await writer.FinishAsync(true); }
    }
    [Fact]
    public async Task FailedStopNeverRenamesActiveFiles()
    {
        var writer = new SegmentWriter(root, new Settings(), Session(), [new(8000, 16, 1), new(8000, 16, 1)], e => throw e);
        var bytes = Signal(400); writer.Offer(0, bytes, bytes.Length); writer.Offer(1, bytes, bytes.Length); writer.Start(); await writer.FinishAsync(false);
        Assert.Equal(2, Directory.GetFiles(root, "*.partial").Length); Assert.Empty(Directory.GetFiles(root, "*.wav"));
    }
    [Fact]
    public async Task ExistingFileIsNeverOverwritten()
    {
        var path = Path.Combine(root, "000001_mic.wav.partial"); var original = new byte[] { 1, 2, 3 }; File.WriteAllBytes(path, original);
        Exception? fault = null;
        var writer = new SegmentWriter(root, new Settings(), Session(), [new(8000, 16, 1), new(8000, 16, 1)], e => fault = e);
        writer.Start(); await writer.FinishAsync(true);
        Assert.NotNull(fault); Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task StartOffsetAndSmallEndPaddingAreExplicit()
    {
        var session = Session();
        session.Tracks[0].StartBeforeTicks = session.ReferenceTicks + Stopwatch.Frequency / 20;
        session.Tracks[1].StartBeforeTicks = session.ReferenceTicks;
        var writer = new SegmentWriter(root, new Settings(), session, [new(8000, 16, 1), new(8000, 16, 1)], e => throw e);
        var bytes = Signal(800); writer.Offer(0, bytes, bytes.Length); writer.Offer(1, bytes, bytes.Length); writer.Start(); await writer.FinishAsync(true);
        Assert.Equal(400, session.Tracks[0].LeadingPaddingFrames);
        Assert.Equal(400, session.Tracks[1].TrailingPaddingFrames);
        Assert.Equal(new byte[800].Concat(bytes).ToArray(), ReadTrack("mic"));
        Assert.Equal(bytes.Concat(new byte[800]).ToArray(), ReadTrack("speaker"));
    }
    [Fact]
    public async Task IndependentRatesPreserveFloatAudioAcrossRoundedBoundaries()
    {
        var session = Session();
        var formats = new[] { WaveFormat.CreateIeeeFloatWaveFormat(44100, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) };
        var writer = new SegmentWriter(root, new Settings { SegmentMinutes = 0.007 }, session, formats, e => throw e);
        var mic = Signal(44100 * 2); var speaker = Signal(48000 * 4);
        writer.Offer(0, mic, mic.Length); writer.Offer(1, speaker, speaker.Length); writer.Start(); await writer.FinishAsync(true);
        Assert.Equal(mic, ReadTrack("mic")); Assert.Equal(speaker, ReadTrack("speaker"));
        Assert.All(session.Segments.Where(s => s.Source == "mic"), s => Assert.Equal(0, s.DataBytes % 4));
        Assert.All(session.Segments.Where(s => s.Source == "speaker"), s => Assert.Equal(0, s.DataBytes % 8));
    }
    [Fact]
    public async Task UnalignedAudioFailsInsteadOfWritingBrokenFrames()
    {
        Exception? fault = null;
        var writer = new SegmentWriter(root, new Settings(), Session(), [new(8000, 16, 1), new(8000, 16, 1)], e => fault = e);
        Assert.False(writer.Offer(0, new byte[3], 3)); Assert.IsType<InvalidDataException>(fault);
        writer.Start(); await writer.FinishAsync(false);
        Assert.Empty(Directory.GetFiles(root, "*.wav"));
    }
    [Fact]
    public void StartupScannerPreservesIncompleteFilesByteForByte()
    {
        var path = Path.Combine(root, "000001_mic.wav.partial"); var original = Signal(20); File.WriteAllBytes(path, original);
        var metadata = Path.Combine(root, "session.json");
        foreach (var broken in new[] { "{broken json", "[]", "{\"status\":12}" })
        {
            File.WriteAllText(metadata, broken);
            IncompleteRecordingScanner.Scan(root);
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(broken, File.ReadAllText(metadata));
        }
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }
    [Theory]
    [InlineData(ConsoleKey.Y, true)] [InlineData(ConsoleKey.N, false)] [InlineData(ConsoleKey.Enter, false)] [InlineData(ConsoleKey.Escape, false)]
    public void OnlyExplicitYApproves(ConsoleKey key, bool approved) => Assert.Equal(approved, Program.IsApproved(key));
    [Theory]
    [InlineData("Zoom.exe", "Zoom")] [InlineData("MS-TEAMS.EXE", "MS-TEAMS")] [InlineData("chrome", "chrome")]
    public void ProcessNamesNormalize(string name, string expected) => Assert.Equal(expected, CallDetector.Normalize(name));
    [Fact]
    public void InvalidSettingsAreRejected() => Assert.Throws<ArgumentException>(() => new Settings { MaxSegmentDataBytes = 2147483648 }.Validate());
}
