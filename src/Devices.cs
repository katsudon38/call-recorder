using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CallRecorder;
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class DeviceManager
{
    public static void List()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            Console.WriteLine($"\n{(flow == DataFlow.Capture ? "入力" : "出力")}デバイス:");
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.All))
                using (device) Console.WriteLine($"  {device.FriendlyName} [{device.State}]\n    ID: {device.ID}");
            CompareDefaults(enumerator, flow);
        }
    }
    private static void CompareDefaults(MMDeviceEnumerator e, DataFlow flow)
    {
        try
        {
            using var communications = e.GetDefaultAudioEndpoint(flow, Role.Communications);
            using var multimedia = e.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            Console.WriteLine($"  通信既定: {communications.FriendlyName}\n  Multimedia既定: {multimedia.FriendlyName}");
            if (communications.ID != multimedia.ID) Console.WriteLine("  注意: 既定ロールが異なります。会議アプリの設定を確認してください。");
        }
        catch (Exception ex) { Console.WriteLine($"  既定デバイスを取得できません: {ex.Message}"); }
    }
    public static MMDevice Resolve(DataFlow flow, string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        var d = string.IsNullOrWhiteSpace(id) ? enumerator.GetDefaultAudioEndpoint(flow, Role.Communications) : enumerator.GetDevice(id);
        if (d.State != DeviceState.Active || d.DataFlow != flow) { d.Dispose(); throw new InvalidOperationException("指定デバイスが無効・切断中、または入出力方向が異なります。"); }
        return d;
    }
    public static long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public static void CheckDirectory(Settings settings)
    {
        var path = settings.DirectoryPath;
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid());
        using (var f = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.WriteByte(0); f.Flush(true); }
        File.Delete(probe);
        if (FreeBytes(path) < settings.MinimumFreeBytes) throw new IOException("空き容量2GiB未満のため開始できません。");
        if (settings.RecordingDirectory != null)
        {
            var rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545") &&
                    (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData)) != 0)
                    Console.WriteLine($"注意: 独自保存先のACLで広いユーザーグループにアクセス権があります: {rule.IdentityReference}");
        }
    }
    public static async Task TestAsync(Settings settings)
    {
        Console.WriteLine("明示テスト: Bluetoothでは音質が変わる場合があります。選択出力に2秒のテスト音を再生し、マイクのピーク値を表示します。録音ファイルは保存しません。");
        using var mic = Resolve(DataFlow.Capture, settings.CaptureDeviceId);
        using var speaker = Resolve(DataFlow.Render, settings.RenderDeviceId);
        using var capture = new WasapiCapture(mic);
        using var output = new WasapiOut(speaker, AudioClientShareMode.Shared, false, 100);
        var tone = new SignalGenerator(48000, 2) { Gain = 0.1, Frequency = 440, Type = SignalGeneratorType.Sin };
        output.Init(tone);
        float maximum = 0;
        capture.DataAvailable += (_, data) =>
        {
            float peak = 0;
            if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                capture.WaveFormat is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                for (int i = 0; i + 4 <= data.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data.Buffer, i)));
            else if (capture.WaveFormat.BitsPerSample == 16)
                for (int i = 0; i + 2 <= data.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data.Buffer, i) / 32768f));
            else Console.WriteLine($"ピーク表示未対応形式: {capture.WaveFormat}");
            maximum = Math.Max(maximum, peak);
        };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        capture.RecordingStopped += (_, e) => { error = e.Exception; stopped.TrySetResult(); };
        try
        {
            capture.StartRecording(); output.Play();
            for (int i = 0; i < 4; i++) { await Task.Delay(500); Console.WriteLine($"マイク最大ピーク: {maximum:P0}"); }
        }
        finally
        {
            capture.StopRecording();
            if (await Task.WhenAny(stopped.Task, Task.Delay(3000)) != stopped.Task) Console.WriteLine("テスト停止タイムアウト");
            output.Stop();
        }
        if (error != null) throw error;
    }
}
public static class CallDetector
{
    public static string Normalize(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    private static readonly HashSet<string> Names = new(["ms-teams", "teams", "zoom", "chrome", "msedge", "firefox"], StringComparer.OrdinalIgnoreCase);
    public static string[] Candidates()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) try { var name = Normalize(p.ProcessName); if (Names.Contains(name)) found.Add(name); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        return found.Order().ToArray();
    }
}
public static class IncompleteRecordingScanner
{
    public static void Scan(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.wav.partial", SearchOption.AllDirectories))
        { var f = new FileInfo(path); Console.WriteLine($"未完了（保持・修復なし）: {f.FullName} / {f.Length} bytes / {f.LastWriteTimeUtc:O}"); }
        foreach (var path in Directory.EnumerateFiles(directory, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.GetProperty("status").GetString() != "completed") Console.WriteLine($"未完了セッション: {path}");
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            { Console.WriteLine($"メタデータ確認不可（保持）: {path}: {ex.Message}"); }
        }
    }
}
