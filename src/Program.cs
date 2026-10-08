using System.Text;
namespace CallRecorder;
public static class Program
{
    private const string ConsentPrompt = "この録音は会議アプリでミュート中もマイクを収録し、独り言や周囲の会話、選択出力の他アプリ音声も保存し得ます。この収録範囲を通知し、必要な同意を得ましたか (Y/N)";
    public static bool IsApproved(ConsoleKey key) => key == ConsoleKey.Y;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows 11用のアプリです。Windows実機で起動してください。"); return 1; }
        if (args.Contains("--help"))
        { Console.WriteLine("CallRecorder: D デバイス一覧 / T テスト音+マイクピーク / R 開始 / S 停止 / Q 安全終了\n--devices: 一覧のみ表示して終了\n設定: " + Paths.Settings); return 0; }
        var recorder = new RecordingCoordinator();
        int exit = 0; bool quitting = false; Task? operation = null;
        using var lifetime = new CancellationTokenSource();
        Task? candidateMonitor = null;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; quitting = true; };
        Console.CancelKeyPress += cancel;
        try
        {
            var settings = Settings.Load();
            Console.WriteLine("CallRecorder 0.1.0 / Windows実機検証版\nD:デバイス一覧  T:音声テスト  R:録音開始  S:停止  Q:安全終了");
            Console.WriteLine($"設定: {Paths.Settings}\n保存先: {settings.DirectoryPath}\n候補は通話の判定ではありません。録音の自動開始・自動停止は行いません。");
            DeviceManager.List();
            if (args.Contains("--devices")) return 0;
            IncompleteRecordingScanner.Scan(settings.DirectoryPath);
            if (Console.IsInputRedirected) { Console.Error.WriteLine("対話コンソールから起動してください。"); return 2; }
            candidateMonitor = MonitorCandidatesAsync(recorder, lifetime.Token);
            while (!quitting)
            {
                if (operation?.IsCompleted == true)
                {
                    try { await operation; } catch (Exception ex) { Console.WriteLine("処理失敗: " + ex.Message); }
                    operation = null;
                }
                if (!Console.KeyAvailable) { await Task.Delay(50); continue; }
                var key = Console.ReadKey(true).Key;
                try
                {
                    switch (key)
                    {
                        case ConsoleKey.D: DeviceManager.List(); break;
                        case ConsoleKey.T:
                            if (recorder.Active || operation != null) { Console.WriteLine("録音・停止中はテストできません。"); break; }
                            settings = Settings.Load(); var testSettings = settings; operation = Task.Run(() => DeviceManager.TestAsync(testSettings)); break;
                        case ConsoleKey.R:
                            if (recorder.Active || operation != null) { Console.WriteLine("録音・停止中です。重複開始できません。"); break; }
                            settings = Settings.Load(); DeviceManager.List();
                            // Resolve and display actual selected IDs before consent; no streams are opened.
                            using (var mic = DeviceManager.Resolve(NAudio.CoreAudioApi.DataFlow.Capture, settings.CaptureDeviceId))
                            using (var speaker = DeviceManager.Resolve(NAudio.CoreAudioApi.DataFlow.Render, settings.RenderDeviceId))
                                Console.WriteLine($"選択マイク: {mic.FriendlyName}\n  {mic.ID}\n選択出力: {speaker.FriendlyName}\n  {speaker.ID}");
                            if (CallDetector.Candidates().Length == 0) Console.WriteLine("通話候補なし。手動録音は可能です。");
                            Console.WriteLine("会議アプリの入力・出力設定が一致しているか確認してください。Bluetoothでは録音・テスト中に音質が変わる場合があります。");
                            Console.WriteLine(ConsentPrompt + "\nYのみ開始。N / Enter / Esc / S は取消、Qは終了。");
                            ConsoleKey answer = ConsoleKey.Escape;
                            while (!quitting)
                            {
                                if (Console.KeyAvailable) { answer = Console.ReadKey(true).Key; break; }
                                await Task.Delay(50);
                            }
                            var consentUtc = DateTimeOffset.UtcNow;
                            Events.Log("consent", new { response = answer == ConsoleKey.Enter ? "" : answer.ToString(), approved = !quitting && IsApproved(answer), promptVersion = "v0.3-1", prompt = ConsentPrompt, utc = consentUtc });
                            if (answer == ConsoleKey.Q) quitting = true;
                            if (!quitting && IsApproved(answer)) { var startSettings = settings; operation = Task.Run(() => recorder.StartAsync(startSettings, consentUtc)); }
                            else Console.WriteLine("録音を開始しませんでした。");
                            break;
                        case ConsoleKey.S:
                            var pending = operation;
                            operation = Task.Run(async () => { if (pending != null) await pending; await recorder.StopAsync("manual-stop"); });
                            break;
                        case ConsoleKey.Q: quitting = true; break;
                    }
                }
                catch (Exception ex) { Console.WriteLine("操作失敗: " + ex.Message); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("起動・実行エラー: " + ex.Message); exit = 1; }
        finally
        {
            if (operation != null) try { await operation; } catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
            await recorder.StopAsync("quit-or-ctrl-c");
            lifetime.Cancel();
            if (candidateMonitor != null) await candidateMonitor;
            Console.CancelKeyPress -= cancel;
        }
        return exit;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task MonitorCandidatesAsync(RecordingCoordinator recorder, CancellationToken token)
    {
        string? last = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var candidates = string.Join(", ", CallDetector.Candidates());
                var displayState = recorder.State == "Idle" && candidates.Length > 0 ? "Candidate" : recorder.State;
                Console.Title = recorder.Active
                    ? "CallRecorder / " + recorder.State + " / マイク常時収録・会議ミュート非連動 / S:停止 Q:終了"
                    : "CallRecorder / " + displayState + " / R:開始 D:デバイス T:テスト";
                if (last != candidates)
                {
                    Console.WriteLine($"通話候補: {(candidates.Length == 0 ? "なし" : candidates)}（通話状態は未判定） / 状態: {displayState}");
                    last = candidates;
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("候補監視エラー: " + ex.Message); }
    }

}
