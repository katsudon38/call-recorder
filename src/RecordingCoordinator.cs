using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallRecorder;
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RecordingCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentQueue<string> errors = new();
    private WasapiCapture[] captures = [];
    private MMDevice[] devices = [];
    private TaskCompletionSource[] stopped = [];
    private WasapiOut? silence;
    private SegmentWriter? writer;
    private SessionInfo? session;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private readonly object callbackGate = new();
    private bool acceptingData;
    private EventHandler<WaveInEventArgs>[] dataHandlers = [];
    private EventHandler<StoppedEventArgs>[] stopHandlers = [];
    private volatile bool stopping;
    private int faultScheduled;
    private volatile string state = "Idle";
    public string State => state;
    public bool Active => state is "Starting" or "Recording" or "Stopping";
    private string directory = "";
    public async Task StartAsync(Settings settings, DateTimeOffset consentUtc)
    {
        await gate.WaitAsync();
        try
        {
            if (Active) { Console.WriteLine("録音または停止処理中です。"); return; }
            state = "Starting"; stopping = false; Interlocked.Exchange(ref faultScheduled, 0); errors.Clear();
            Events.Log("state", state);
            DeviceManager.CheckDirectory(settings);
            devices = [DeviceManager.Resolve(DataFlow.Capture, settings.CaptureDeviceId)];
            devices = [devices[0], DeviceManager.Resolve(DataFlow.Render, settings.RenderDeviceId)];
            Console.WriteLine($"録音対象: マイク={devices[0].FriendlyName} / 出力={devices[1].FriendlyName}");
            captures = [new WasapiCapture(devices[0])];
            captures = [captures[0], new WasapiLoopbackCapture(devices[1])];
            session = new SessionInfo { ConsentUtc = consentUtc, DeviceConfiguration = settings.DeviceConfiguration,
                Tracks = devices.Select((d, t) => new TrackInfo { Source = t == 0 ? "mic" : "speaker", DeviceId = d.ID,
                    DeviceName = d.FriendlyName, Format = captures[t].WaveFormat.ToString(), SampleRate = captures[t].WaveFormat.SampleRate,
                    BlockAlign = captures[t].WaveFormat.BlockAlign }).ToArray() };
            directory = Path.Combine(settings.DirectoryPath, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + session.SessionId);
            Directory.CreateDirectory(directory);
            // Claim the new session exclusively; no existing recording is overwritten.
            using (new FileStream(Path.Combine(directory, ".session-lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            AtomicJson.Write(Path.Combine(directory, "session.json"), session);
            writer = new SegmentWriter(directory, settings, session, captures.Select(c => c.WaveFormat).ToArray(), Fault);
            stopped = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
            dataHandlers = new EventHandler<WaveInEventArgs>[2];
            stopHandlers = new EventHandler<StoppedEventArgs>[2];
            acceptingData = true;
            for (int t = 0; t < 2; t++)
            {
                var track = t;
                dataHandlers[t] = (_, e) =>
                {
                    lock (callbackGate)
                    {
                    if (!acceptingData || e.BytesRecorded == 0) return;
                    var info = session.Tracks[track];
                    var now = Stopwatch.GetTimestamp(); info.FirstCallbackTicks ??= now; info.LastCallbackTicks = now;
                    if (writer.Offer(track, e.Buffer, e.BytesRecorded)) Interlocked.Add(ref infoCaptured[track], e.BytesRecorded / info.BlockAlign);
                    }
                };
                captures[t].DataAvailable += dataHandlers[t];
                stopHandlers[t] = (_, e) =>
                {
                    lock (callbackGate)
                    {
                    if (!acceptingData) return;
                    if (e.Exception != null) Fault(e.Exception);
                    else if (!stopping) Fault(new IOException($"{session.Tracks[track].Source}: 予期しないキャプチャ停止"));
                    stopped[track].TrySetResult();
                    }
                };
                captures[t].RecordingStopped += stopHandlers[t];
            }
            Array.Clear(infoCaptured);
            silence = new WasapiOut(devices[1], AudioClientShareMode.Shared, false, 100);
            silence.Init(new SilenceProvider(captures[1].WaveFormat)); silence.Play();
            session.ReferenceTicks = Stopwatch.GetTimestamp();
            for (int t = 0; t < 2; t++)
            {
                session.Tracks[t].StartBeforeTicks = Stopwatch.GetTimestamp();
                captures[t].StartRecording();
                session.Tracks[t].StartAfterTicks = Stopwatch.GetTimestamp();
            }
            writer.Start();
            session.Status = "recording"; state = "Recording";
            monitorCancellation = new();
            monitorTask = MonitorAsync(settings, monitorCancellation.Token);
            Console.WriteLine($"録音中：マイク常時収録（会議アプリのミュートに連動しません）\n会議アプリのミュート状態：未取得\n保存先: {directory}");
            Events.Log("started", new { directory, state });
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.Message); Console.WriteLine($"開始失敗: {ex.Message}");
            // A consumer must run even on partial startup to drain/retain captured data.
            if (writer != null && session?.Status == "starting") writer.Start();
            await StopCoreAsync("start-failed");
        }
        finally { gate.Release(); }
    }
    private readonly long[] infoCaptured = new long[2];
    private void Fault(Exception ex)
    {
        errors.Enqueue(ex.Message);
        writer?.MarkFailed();
        if (Interlocked.Exchange(ref faultScheduled, 1) == 0)
        {
            var origin = session;
            _ = Task.Run(async () =>
            {
                Console.WriteLine($"\n障害: {ex.Message}");
                await gate.WaitAsync();
                try { if (origin != null && ReferenceEquals(session, origin)) await StopCoreAsync("error"); }
                finally { gate.Release(); }
            });
        }
    }
    private async Task MonitorAsync(Settings settings, CancellationToken token)
    {
        var warnings = new bool[2]; int count = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                for (int t = 0; t < 2; t++)
                {
                    var info = session!.Tracks[t];
                    var since = (Stopwatch.GetTimestamp() - (info.LastCallbackTicks == 0 ? info.StartAfterTicks : info.LastCallbackTicks)) / (double)Stopwatch.Frequency;
                    if (since >= 5) { Fault(new IOException($"{info.Source}: 5秒間データ未着")); return; }
                    if (since >= 2 && !warnings[t]) { warnings[t] = true; Console.WriteLine($"警告: {info.Source} のデータが2秒未着"); }
                    if (since < 2) warnings[t] = false;
                    if (devices[t].State != DeviceState.Active) { Fault(new IOException($"{info.Source}: デバイス切断")); return; }
                    if (writer!.QueuedBytes(t) / (double)captures[t].WaveFormat.AverageBytesPerSecond >= settings.QueueWarningSeconds)
                        Console.WriteLine($"警告: {info.Source} 書き込みキュー滞留 {writer.QueuedBytes(t)} bytes");
                }
                using var enumerator = new MMDeviceEnumerator();
                foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                {
                    try
                    {
                        using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
                        var t = flow == DataFlow.Capture ? 0 : 1;
                        if (defaultIds[t] != d.ID)
                        { if (defaultIds[t] != null) { Console.WriteLine($"既定変更通知: {flow} → {d.FriendlyName}。収録対象IDを維持します。"); Events.Log("default-device-changed", new { flow = flow.ToString(), id = d.ID }); } defaultIds[t] = d.ID; }
                    }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (++count % 5 == 0 && DeviceManager.FreeBytes(directory) < 536870912) { Fault(new IOException("空き容量512MiB未満")); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fault(ex); }
    }
    private readonly string?[] defaultIds = new string?[2];
    public async Task StopAsync(string reason)
    {
        await gate.WaitAsync();
        try { if (state == "Idle" && writer == null && captures.Length == 0) return; await StopCoreAsync(reason); }
        finally { gate.Release(); }
    }
    private async Task StopCoreAsync(string reason)
    {
        var watch = Stopwatch.StartNew(); stopping = true; state = "Stopping";
        monitorCancellation?.Cancel();
        if (monitorTask != null) { await monitorTask; monitorTask = null; }
        Console.WriteLine("停止処理中：キャプチャ停止と残データの保存を待っています。");
        for (int t = 0; t < captures.Length; t++)
        {
            try { captures[t].StopRecording(); } catch (Exception ex) { errors.Enqueue(ex.Message); }
        }
        if (stopped.Length > 0)
        {
            var all = Task.WhenAll(stopped.Select(s => s.Task));
            if (await Task.WhenAny(all, Task.Delay(3000)) != all) errors.Enqueue("キャプチャ停止イベント: 3秒タイムアウト");
        }
        try { silence?.Stop(); silence?.Dispose(); } catch (Exception ex) { errors.Enqueue(ex.Message); }
        silence = null;
        lock (callbackGate)
        {
            acceptingData = false;
            for (int t = 0; t < captures.Length; t++)
            {
                if (t < dataHandlers.Length) captures[t].DataAvailable -= dataHandlers[t];
                if (t < stopHandlers.Length) captures[t].RecordingStopped -= stopHandlers[t];
            }
        }
        // Detach callbacks after stop completion/timeout, then drain the sole writer.
        foreach (var capture in captures) try { capture.Dispose(); } catch (Exception ex) { errors.Enqueue(ex.Message); }
        captures = [];
        if (writer != null)
        {
            try { await writer.FinishAsync(errors.IsEmpty); } catch (Exception ex) { errors.Enqueue(ex.Message); }
        }
        foreach (var device in devices) try { device.Dispose(); } catch (Exception ex) { errors.Enqueue(ex.Message); }
        devices = []; stopped = [];
        monitorCancellation?.Dispose(); monitorCancellation = null;
        if (session != null)
        {
            for (int t = 0; t < 2; t++) session.Tracks[t].CapturedFrames = Interlocked.Read(ref infoCaptured[t]);
            session.EndedUtc = DateTimeOffset.UtcNow; session.StopReason = reason; session.Errors = errors.ToList();
            session.StopMilliseconds = watch.Elapsed.TotalMilliseconds;
            session.Status = errors.IsEmpty ? "completed" : "failed";
            try { AtomicJson.Write(Path.Combine(directory, "session.json"), session); }
            catch (Exception ex) { errors.Enqueue("メタデータ確定失敗: " + ex.Message); }
        }
        state = errors.IsEmpty ? "Idle" : "Error";
        Console.WriteLine(errors.IsEmpty ? $"保存完了 ({watch.Elapsed.TotalSeconds:F2}秒): {directory}" : "録音失敗。未完了データを保持しました: " + string.Join(" / ", errors));
        try { Events.Log("stopped", new { reason, directory, status = state, errors = errors.ToArray(), elapsedMs = watch.Elapsed.TotalMilliseconds }); }
        catch (Exception ex) { Console.WriteLine("ログ保存失敗: " + ex.Message); }
        writer = null; session = null; dataHandlers = []; stopHandlers = []; Array.Clear(defaultIds); state = "Idle";
    }
}
