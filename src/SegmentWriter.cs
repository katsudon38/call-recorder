using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;

namespace CallRecorder;

// The consumer exclusively owns all file streams, WAV headers and metadata writes.
public sealed class SegmentWriter
{
    private sealed record Block(int Track, byte[] Data);
    private sealed class OpenFile(FileStream stream, WaveFileWriter writer, SegmentInfo info)
    {
        public FileStream Stream = stream;
        public WaveFileWriter Writer = writer;
        public SegmentInfo Info = info;
    }
    private readonly Channel<Block> channel = Channel.CreateUnbounded<Block>(new() { SingleReader = true });
    private readonly object queueGate = new();
    private readonly long[] queued = new long[2];
    private readonly long[] frames = new long[2];
    private readonly long[] segmentFrames;
    private readonly WaveFormat[] formats;
    private readonly Dictionary<int, OpenFile[]> files = [];
    private readonly string directory;
    private readonly Settings settings;
    private readonly SessionInfo session;
    private readonly Action<Exception> fault;
    private bool accepting = true;
    private bool failed;
    private Task? consumer;
    private long lastHeader = Stopwatch.GetTimestamp();
    private long lastDurable = Stopwatch.GetTimestamp();
    public SegmentWriter(string directory, Settings settings, SessionInfo session, WaveFormat[] formats, Action<Exception> fault)
    {
        this.directory = directory; this.settings = settings; this.session = session; this.formats = formats; this.fault = fault;
        var seconds = Math.Min(settings.SegmentMinutes * 60, formats.Min(f => settings.MaxSegmentDataBytes / (double)f.AverageBytesPerSecond));
        // A common whole-millisecond boundary is exactly representable by normal audio rates.
        seconds = Math.Floor(seconds * 1000) / 1000;
        segmentFrames = formats.Select(f => (long)Math.Floor(seconds * f.SampleRate)).ToArray();
        if (segmentFrames.Any(f => f <= 0)) throw new ArgumentException("分割長が短すぎます。");
        session.SegmentSeconds = seconds;
    }
    public void Start() => consumer = Task.Run(RunAsync);
    public long QueuedBytes(int track) { lock (queueGate) return queued[track]; }
    public bool Offer(int track, byte[] buffer, int length)
    {
        Exception? problem = null;
        lock (queueGate)
        {
            if (!accepting) return false;
            if (length % formats[track].BlockAlign != 0) problem = new InvalidDataException("非フレーム境界の音声ブロック");
            else if (queued[track] + length > formats[track].AverageBytesPerSecond * settings.QueueCapacitySeconds)
                problem = new IOException($"{session.Tracks[track].Source}: キュー上限超過。データを成功扱いせず停止します。");
            if (problem == null)
            {
                var copy = new byte[length]; Buffer.BlockCopy(buffer, 0, copy, 0, length);
                queued[track] += length;
                session.Tracks[track].MaximumQueuedBytes = Math.Max(session.Tracks[track].MaximumQueuedBytes, queued[track]);
                if (channel.Writer.TryWrite(new(track, copy))) return true;
                queued[track] -= length;
                problem = new IOException("書き込みキューが終了しています。");
            }
            accepting = false; failed = true;
        }
        fault(problem); return false;
    }
    public void MarkFailed() { lock (queueGate) failed = true; }
    public async Task FinishAsync(bool success)
    {
        lock (queueGate) { accepting = false; failed |= !success; channel.Writer.TryComplete(); }
        if (consumer != null) await consumer;
    }
    private async Task RunAsync()
    {
        try
        {
            OpenSegment(1);
            for (int t = 0; t < 2; t++)
            {
                var offset = Math.Max(0, (session.Tracks[t].StartBeforeTicks - session.ReferenceTicks) / (double)Stopwatch.Frequency);
                var padding = (long)Math.Round(offset * formats[t].SampleRate);
                if (offset > 2) throw new IOException("開始差が2秒超過。開始失敗として停止します。");
                session.Tracks[t].LeadingPaddingFrames = padding;
                WriteZeros(t, padding);
            }
            Task<bool>? pendingRead = null;
            while (true)
            {
                while (channel.Reader.TryRead(out var block))
                {
                    var clock = Stopwatch.StartNew();
                    WriteBlock(block.Track, block.Data, 0, block.Data.Length);
                    session.Tracks[block.Track].CapturedFrames += block.Data.Length / formats[block.Track].BlockAlign;
                    session.MaximumWriteMilliseconds = Math.Max(session.MaximumWriteMilliseconds, clock.Elapsed.TotalMilliseconds);
                    lock (queueGate) queued[block.Track] -= block.Data.Length;
                    FlushIfDue();
                }
                FlushIfDue();
                pendingRead ??= channel.Reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(pendingRead, Task.Delay(100)) == pendingRead)
                {
                    if (!await pendingRead) break;
                    pendingRead = null;
                }
            }
            if (!failed)
            {
                // Small end alignment only; do not hide data stalls or clock drift with padding.
                var ends = frames.Select((f, t) => f / (double)formats[t].SampleRate).ToArray();
                var delta = Math.Abs(ends[0] - ends[1]);
                if (delta <= 0.25)
                {
                    var t = ends[0] < ends[1] ? 0 : 1;
                    var pad = (long)Math.Round(delta * formats[t].SampleRate);
                    session.Tracks[t].TrailingPaddingFrames = pad; WriteZeros(t, pad);
                }
            }
            foreach (var number in files.Keys.Order().ToArray())
            {
                if (!failed && files[number].All(f => f.Info.Frames == 0) && number > 1)
                {
                    foreach (var file in files[number])
                    { file.Writer.Dispose(); file.Stream.Dispose(); File.Delete(Path.Combine(directory, file.Info.FileName)); session.Segments.Remove(file.Info); }
                    files.Remove(number);
                }
                else CloseSegment(number, !failed);
            }
        }
        catch (Exception ex)
        {
            lock (queueGate) { accepting = false; failed = true; }
            fault(ex);
            // Dispose updates headers where possible, but failed files stay .partial.
            foreach (var pair in files.Values)
                foreach (var file in pair)
                {
                    file.Info.Status = "failed";
                    try { file.Writer.Dispose(); } catch { }
                    try { file.Stream.Dispose(); } catch { }
                }
            files.Clear();
        }
        finally
        {
            while (channel.Reader.TryRead(out var discarded)) lock (queueGate) queued[discarded.Track] -= discarded.Data.Length;
            SaveMetadata();
        }
    }
    private void WriteZeros(int t, long count)
    {
        var zeros = new byte[formats[t].BlockAlign * 4096];
        while (count > 0) { var chunk = (int)Math.Min(count, 4096); WriteBlock(t, zeros, 0, chunk * formats[t].BlockAlign); count -= chunk; }
    }
    private void WriteBlock(int t, byte[] data, int offset, int count)
    {
        var align = formats[t].BlockAlign;
        while (count > 0)
        {
            var number = checked((int)(frames[t] / segmentFrames[t]) + 1);
            OpenSegment(number);
            var take = (int)Math.Min(count, (segmentFrames[t] - frames[t] % segmentFrames[t]) * align);
            var file = files[number][t];
            file.Writer.Write(data, offset, take);
            file.Info.DataBytes += take; file.Info.Frames += take / align;
            frames[t] += take / align;
            file.Info.TimelineEndSeconds = frames[t] / (double)formats[t].SampleRate;
            offset += take; count -= take;
            // Prepare both next files before closing the old pair.
            var completed = (int)Math.Min(frames[0] / segmentFrames[0], frames[1] / segmentFrames[1]);
            foreach (var old in files.Keys.Where(n => n <= completed).Order().ToArray())
            { OpenSegment(old + 1); CloseSegment(old, !failed); }
        }
    }
    private void OpenSegment(int number)
    {
        if (files.ContainsKey(number)) return;
        var pair = new OpenFile[2];
        try
        {
            for (int t = 0; t < 2; t++)
            {
                var name = $"{number:D6}_{session.Tracks[t].Source}.wav.partial";
                var info = new SegmentInfo { Source = session.Tracks[t].Source, Number = number, FileName = name,
                    TimelineStartSeconds = (number - 1) * segmentFrames[t] / (double)formats[t].SampleRate };
                var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                WaveFileWriter writer;
                try { writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), formats[t]); }
                catch { stream.Dispose(); throw; }
                pair[t] = new(stream, writer, info); session.Segments.Add(info);
            }
            files.Add(number, pair);
        }
        catch
        {
            foreach (var file in pair.Where(x => x != null)) { file.Writer.Dispose(); file.Stream.Dispose(); file.Info.Status = "failed"; }
            throw;
        }
    }
    private void CloseSegment(int number, bool success)
    {
        var pair = files[number];
        foreach (var file in pair) { file.Writer.Dispose(); file.Stream.Flush(true); file.Stream.Dispose(); }
        if (success)
            foreach (var file in pair)
            {
                var final = file.Info.FileName[..^8];
                File.Move(Path.Combine(directory, file.Info.FileName), Path.Combine(directory, final));
                file.Info.FileName = final; file.Info.Status = "completed";
            }
        else foreach (var file in pair) file.Info.Status = "recoverable";
        files.Remove(number); SaveMetadata();
    }
    private void FlushIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        var header = (now - lastHeader) / (double)Stopwatch.Frequency >= settings.HeaderFlushSeconds;
        var durable = (now - lastDurable) / (double)Stopwatch.Frequency >= settings.DurableFlushSeconds;
        if (!header && !durable) return;
        var watch = Stopwatch.StartNew();
        foreach (var pair in files.Values) foreach (var file in pair)
        { file.Writer.Flush(); if (durable) file.Stream.Flush(true); }
        session.MaximumFlushMilliseconds = Math.Max(session.MaximumFlushMilliseconds, watch.Elapsed.TotalMilliseconds);
        SaveMetadata();
        if (header) lastHeader = Stopwatch.GetTimestamp();
        if (durable) lastDurable = Stopwatch.GetTimestamp();
    }
    public void SaveMetadata() => AtomicJson.Write(Path.Combine(directory, "session.json"), session);
}
