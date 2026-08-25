using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using NexusMonach.Models;

namespace NexusMonach.Services;

public enum VoiceAnnouncementPriority
{
    Progress,
    Important,
    Critical
}

public sealed record VoiceAnnouncement(
    string Text,
    VoiceAnnouncementPriority Priority = VoiceAnnouncementPriority.Important,
    bool IsPrivateWindow = false);

public static partial class VoiceAssistantService
{
    private static readonly BlockingCollection<VoiceQueueItem> Queue = new(24);
    private static readonly object Sync = new();
    private static Thread? _thread;
    private static SpeechSynthesizer? _activeSynthesizer;
    private static object? _activeSapiVoice;
    private static volatile bool _isSpeaking;
    private static bool _shutdown;

    public static bool IsSpeaking => _isSpeaking;
    public static bool IsBusy => _isSpeaking || Queue.Count > 0;
    public static string EngineStatus => NeuralVoiceService.Status;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_thread is { IsAlive: true }) return;
            _shutdown = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Nexus Voice female synthesis"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public static bool Announce(string text,
        VoiceAnnouncementPriority priority = VoiceAnnouncementPriority.Important,
        bool isPrivateWindow = false)
    {
        var settings = SettingsService.Current;
        if (!ShouldSpeak(settings.VoiceAssistantMode, priority, isPrivateWindow)) return false;
        var safe = SanitizeForSpeech(text);
        if (safe.Length == 0) return false;
        Initialize();
        var item = new VoiceQueueItem(safe, priority, false, null, null, settings.NeuralVoiceProfile);
        if (Queue.TryAdd(item)) return true;
        if (priority != VoiceAnnouncementPriority.Critical) return false;
        DrainPendingQueue();
        return Queue.TryAdd(item);
    }

    /// <summary>
    /// Произносит явно запрошенный пользователем результат и возвращается только
    /// после завершения речи. Это отдельный путь для видеоперевода: он работает
    /// даже при выключенных фоновых объявлениях и не превращает приватное окно в
    /// источник телеметрии — текст остаётся внутри локального процесса SAPI.
    /// </summary>
    public static async Task<bool> SpeakAndWaitAsync(string text,
        VoiceAnnouncementPriority priority = VoiceAnnouncementPriority.Important,
        bool isPrivateWindow = false,
        bool userInitiated = false,
        int? rateOverride = null,
        CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Current;
        if (!userInitiated && !ShouldSpeak(settings.VoiceAssistantMode, priority, isPrivateWindow))
            return false;
        var safe = SanitizeForSpeech(text);
        if (safe.Length == 0) return false;

        Initialize();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.TryAdd(new VoiceQueueItem(safe, priority, false, completion, rateOverride,
                settings.NeuralVoiceProfile))) return false;
        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StopSpeaking();
            throw;
        }
    }

    public static void SpeakTestPhrase() =>
        Announce("Nexus Monach готов. Сегодня 02.08.2026. WebView2 работает. Чем я могу помочь?",
            VoiceAnnouncementPriority.Critical);

    public static void StopSpeaking()
    {
        Initialize();
        DrainPendingQueue();
        lock (Sync)
        {
            try { _activeSynthesizer?.SpeakAsyncCancelAll(); } catch { }
            try
            {
                if (_activeSapiVoice is not null)
                {
                    dynamic sapi = _activeSapiVoice;
                    sapi.Speak(string.Empty, 3); // async + purge queued speech
                }
            }
            catch { }
        }
        NeuralVoiceService.Stop();
        Queue.TryAdd(new VoiceQueueItem(string.Empty, VoiceAnnouncementPriority.Critical, true, null, null,
            SettingsService.Current.NeuralVoiceProfile));
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_shutdown) return;
            _shutdown = true;
            DrainPendingQueue();
            Queue.TryAdd(new VoiceQueueItem(string.Empty, VoiceAnnouncementPriority.Critical, true, null, null,
                SettingsService.Current.NeuralVoiceProfile));
            Queue.CompleteAdding();
        }
        NeuralVoiceService.Shutdown();
    }

    internal static bool ShouldSpeak(VoiceAssistantMode mode, VoiceAnnouncementPriority priority,
        bool isPrivateWindow)
    {
        if (isPrivateWindow || mode == VoiceAssistantMode.Off) return false;
        return mode == VoiceAssistantMode.Assistant || priority != VoiceAnnouncementPriority.Progress;
    }

    internal static string SanitizeForSpeech(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = UrlPattern().Replace(value, " ссылка ");
        text = EmailPattern().Replace(text, " адрес почты ");
        text = SecretPattern().Replace(text, "$1 скрыто");
        text = ControlPattern().Replace(text, " ");
        text = SpacePattern().Replace(text, " ").Trim();
        text = RussianSpeechTextNormalizer.Normalize(text);
        if (text.Length <= 360) return text;
        var boundary = text.LastIndexOf(' ', 359);
        return text[..(boundary >= 240 ? boundary : 360)].TrimEnd();
    }

    private static void Run()
    {
        SpeechSynthesizer? synthesizer = null;
        try
        {
            try
            {
                synthesizer = new SpeechSynthesizer();
                SelectFemaleVoice(synthesizer);
                synthesizer.SetOutputToDefaultAudioDevice();
                synthesizer.Volume = 95;
            }
            catch (PlatformNotSupportedException ex)
            {
                // Self-contained Windows packages can legitimately have no
                // System.Speech backend. Neural TTS is the primary engine and
                // must remain usable without creating SAPI first.
                try { synthesizer?.Dispose(); } catch { }
                synthesizer = null;
                CrashReportService.RecordNonFatal("voice", "sapi-unavailable", ex);
            }
            lock (Sync) _activeSynthesizer = synthesizer;
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                if (item.Cancel)
                {
                    try { synthesizer?.SpeakAsyncCancelAll(); } catch { }
                    item.Completion?.TrySetResult(false);
                    continue;
                }
                if (_shutdown)
                {
                    item.Completion?.TrySetResult(false);
                    break;
                }
                try
                {
                    _isSpeaking = true;
                    var rate = Math.Clamp(item.RateOverride ?? SettingsService.Current.VoiceRate, -4, 4);
                    if (NeuralVoiceService.TrySpeak(item.Text, item.Profile, rate))
                    {
                        item.Completion?.TrySetResult(true);
                        continue;
                    }
                    if (synthesizer is null)
                    {
                        SpeakWithNativeSapi(item.Text, rate);
                        item.Completion?.TrySetResult(true);
                        continue;
                    }
                    synthesizer.Rate = rate;
                    using var completed = new ManualResetEventSlim(false);
                    SpeakCompletedEventArgs? result = null;
                    EventHandler<SpeakCompletedEventArgs> handler = (_, args) =>
                    {
                        result = args;
                        completed.Set();
                    };
                    synthesizer.SpeakCompleted += handler;
                    try
                    {
                        synthesizer.SpeakAsync(item.Text);
                        completed.Wait();
                    }
                    finally { synthesizer.SpeakCompleted -= handler; }
                    if (result?.Error is not null) throw result.Error;
                    item.Completion?.TrySetResult(result?.Cancelled != true);
                }
                catch (Exception ex)
                {
                    item.Completion?.TrySetException(ex);
                    CrashReportService.RecordNonFatal("voice", "speech-synthesis", ex);
                }
                finally { _isSpeaking = false; }
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("voice", "speech-thread", ex);
        }
        finally
        {
            lock (Sync) _activeSynthesizer = null;
            try { synthesizer?.Dispose(); } catch { }
            _isSpeaking = false;
        }
    }

    private static void SpeakWithNativeSapi(string text, int rate)
    {
        object? voiceObject = null;
        VideoDubbingVoiceService.SuspendPlayback();
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice")
                           ?? throw new PlatformNotSupportedException(
                               "Windows SAPI.SpVoice не зарегистрирован.");
            voiceObject = Activator.CreateInstance(sapiType)
                          ?? throw new InvalidOperationException("Windows не создал SAPI.SpVoice.");
            dynamic voice = voiceObject;
            dynamic russianFemale = voice.GetVoices("Language=419;Gender=Female", string.Empty);
            dynamic russian = voice.GetVoices("Language=419", string.Empty);
            dynamic female = voice.GetVoices("Gender=Female", string.Empty);
            dynamic all = voice.GetVoices(string.Empty, string.Empty);
            if (russianFemale.Count > 0) voice.Voice = russianFemale.Item(0);
            else if (russian.Count > 0) voice.Voice = russian.Item(0);
            else if (female.Count > 0) voice.Voice = female.Item(0);
            else if (all.Count > 0) voice.Voice = all.Item(0);
            else throw new InvalidOperationException("Windows SAPI не вернул доступных голосов.");
            voice.Rate = Math.Clamp(rate, -4, 4);
            voice.Volume = 95;
            lock (Sync) _activeSapiVoice = voiceObject;
            voice.Speak(text, 0); // synchronous on the dedicated STA voice thread
        }
        finally
        {
            lock (Sync)
                if (ReferenceEquals(_activeSapiVoice, voiceObject)) _activeSapiVoice = null;
            if (voiceObject is not null && Marshal.IsComObject(voiceObject))
                try { Marshal.FinalReleaseComObject(voiceObject); } catch { }
            VideoDubbingVoiceService.ResumePlayback();
        }
    }

    internal static string? SelectFemaleVoice(SpeechSynthesizer synthesizer)
    {
        var installed = synthesizer.GetInstalledVoices().Where(item => item.Enabled).ToArray();
        var candidates = installed.Select(item => new NexusVoiceCandidate(
            item.VoiceInfo.Name,
            item.VoiceInfo.Culture.Name,
            item.VoiceInfo.Gender switch
            {
                VoiceGender.Female => NexusVoiceGender.Female,
                VoiceGender.Male => NexusVoiceGender.Male,
                _ => NexusVoiceGender.Unknown
            })).ToArray();
        var index = VoiceProfileSelector.SelectPreferredIndex(candidates);
        if (index < 0) return null;
        synthesizer.SelectVoice(installed[index].VoiceInfo.Name);
        return installed[index].VoiceInfo.Name;
    }

    private static void DrainPendingQueue()
    {
        while (Queue.TryTake(out var pending)) pending.Completion?.TrySetResult(false);
    }

    private sealed record VoiceQueueItem(string Text, VoiceAnnouncementPriority Priority, bool Cancel,
        TaskCompletionSource<bool>? Completion, int? RateOverride, NeuralVoiceProfile Profile);

    [GeneratedRegex(@"(?i)\b(?:https?|wss?)://[^\s]+|\bwww\.[^\s]+")]
    private static partial Regex UrlPattern();
    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b")]
    private static partial Regex EmailPattern();
    [GeneratedRegex(@"(?i)\b(token|cookie|authorization|password|пароль|секрет)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex SecretPattern();
    [GeneratedRegex(@"[\p{C}\r\n\t]+")]
    private static partial Regex ControlPattern();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacePattern();
}
