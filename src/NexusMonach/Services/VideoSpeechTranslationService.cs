namespace NexusMonach.Services;

/// <summary>
/// Text-only boundary between a video audio source and Nexus' local voice output.
/// Providers may recognize and translate locally or remotely, but they can never
/// return synthesized audio. Every audible response is produced by
/// <see cref="VideoDubbingVoiceService"/> on this device.
/// </summary>
internal static class VideoSpeechTranslationService
{
    public const bool AllowsRemoteSynthesizedAudio = false;
    public const bool RequiresLocalVoiceOutput = true;

    internal static async Task<VideoSpeechTranslationText?> TranslateToRussianTextAsync(
        LiveAudioSegment segment, string transcript, string transcriptWindow,
        string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var translated = await LocalIntelligenceService.TranslateToRussianAsync(
            transcript, cancellationToken, sourceLanguage);
        if (string.IsNullOrWhiteSpace(translated)) return null;

        return new VideoSpeechTranslationText(
            transcript, transcriptWindow, translated, sourceLanguage);
    }

    internal static string RemoveTranscriptOverlap(string previous, string current)
    {
        current = System.Text.RegularExpressions.Regex.Replace(
            current ?? string.Empty, @"\s+", " ").Trim();
        previous = System.Text.RegularExpressions.Regex.Replace(
            previous ?? string.Empty, @"\s+", " ").Trim();
        if (current.Length == 0 || previous.Length == 0) return current;
        if (previous.Equals(current, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var oldWords = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var newWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maximum = Math.Min(Math.Min(oldWords.Length, newWords.Length), 12);
        for (var overlap = maximum; overlap >= 2; overlap--)
        {
            var matches = true;
            for (var index = 0; index < overlap; index++)
            {
                var oldWord = NormalizeOverlapWord(oldWords[oldWords.Length - overlap + index]);
                var newWord = NormalizeOverlapWord(newWords[index]);
                if (oldWord.Length == 0 || !oldWord.Equals(
                        newWord, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return string.Join(' ', newWords.Skip(overlap));
        }
        return current;
    }

    private static string NormalizeOverlapWord(string word) =>
        new(word.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Keeps rolling Whisper overlap and unfinished sentence fragments out of the
/// translator. OPUS receives a complete short clause instead of isolated words;
/// a fragment is held for at most one following audio window.
/// </summary>
internal sealed class VideoSpeechTranslationContext
{
    private string _previousWindow = string.Empty;
    private string _pending = string.Empty;
    private string _pendingLanguage = string.Empty;
    private int _pendingParts;

    public async Task<VideoSpeechTranslationText?> TranslateAsync(
        LiveAudioSegment segment, CancellationToken cancellationToken = default)
    {
        var speech = await NexusFabricRuntime.TranscribeSpeechDetailedAsync(
            segment.Wav, cancellationToken, WhisperLane.Dubbing);
        var transcriptWindow = WhisperService.NormalizeTranscript(speech.Text);
        var delta = VideoSpeechTranslationService.RemoveTranscriptOverlap(
            _previousWindow, transcriptWindow);
        _previousWindow = transcriptWindow;
        if (string.IsNullOrWhiteSpace(delta))
            return _pending.Length == 0
                ? null
                : await TranslatePendingAsync(segment, transcriptWindow,
                    speech.Language, cancellationToken);

        if (_pending.Length == 0) _pendingLanguage = speech.Language;
        var fresh = VideoSpeechTranslationService.RemoveTranscriptOverlap(_pending, delta);
        if (fresh.Length > 0)
        {
            _pending = JoinFragments(_pending, fresh);
            _pendingParts++;
        }
        if (!ShouldFlush(_pending, _pendingParts)) return null;

        return await TranslatePendingAsync(segment, transcriptWindow,
            speech.Language, cancellationToken);
    }

    private async Task<VideoSpeechTranslationText?> TranslatePendingAsync(
        LiveAudioSegment segment, string transcriptWindow, string fallbackLanguage,
        CancellationToken cancellationToken)
    {
        var complete = _pending;
        var language = string.IsNullOrWhiteSpace(_pendingLanguage)
            ? fallbackLanguage
            : _pendingLanguage;
        _pending = string.Empty;
        _pendingLanguage = string.Empty;
        _pendingParts = 0;
        return await VideoSpeechTranslationService.TranslateToRussianTextAsync(
            segment, complete, transcriptWindow, language, cancellationToken);
    }

    internal static bool ShouldFlush(string? text, int fragmentCount)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(
                text, @"[.!?…][""'»)]?$"))
            return true;
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount >= 9 || text.Length >= 72 || fragmentCount >= 2;
    }

    internal static string JoinFragments(string? first, string? second)
    {
        first = WhisperService.NormalizeTranscript(first);
        second = WhisperService.NormalizeTranscript(second);
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        return $"{first.TrimEnd()} {second.TrimStart()}";
    }
}

internal sealed record LiveAudioSegment(byte[] Wav, DateTimeOffset CapturedAt);

internal sealed record VideoSpeechTranslationText(
    string Transcript,
    string TranscriptWindow,
    string RussianText,
    string SourceLanguage);

/// <summary>
/// Suppresses a phrase only while it is still part of the recent rolling audio
/// window. Exact, contained and lightly reworded variants are treated as the
/// same phrase, preventing Whisper/translation overlap from creating A-B-A loops.
/// </summary>
internal sealed class RecentVideoPhraseGuard(int capacity = 8,
    int retentionSeconds = 35)
{
    private readonly Queue<PhraseEntry> _recent = new();
    private readonly int _capacity = Math.Clamp(capacity, 2, 20);
    private readonly TimeSpan _retention = TimeSpan.FromSeconds(
        Math.Clamp(retentionSeconds, 5, 120));

    public bool IsNovel(string? text, DateTimeOffset now)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;

        while (_recent.TryPeek(out var oldest) && now - oldest.SeenAt > _retention)
            _recent.Dequeue();

        if (_recent.Any(entry => IsSamePhrase(entry.Normalized, normalized)))
            return false;

        _recent.Enqueue(new PhraseEntry(normalized, now));
        while (_recent.Count > _capacity) _recent.Dequeue();
        return true;
    }

    internal static bool IsSamePhrase(string? first, string? second)
    {
        first = Normalize(first);
        second = Normalize(second);
        if (first.Length == 0 || second.Length == 0) return false;
        if (first.Equals(second, StringComparison.Ordinal)) return true;

        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length > second.Length ? first : second;
        if (shorter.Length >= 12 && longer.Contains(shorter, StringComparison.Ordinal) &&
            shorter.Length / (double)longer.Length >= 0.65)
            return true;

        var firstWords = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var secondWords = second.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (Math.Min(firstWords.Count, secondWords.Count) < 3) return false;
        var common = firstWords.Count(secondWords.Contains);
        var dice = 2.0 * common / (firstWords.Count + secondWords.Count);
        return dice >= 0.84;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var characters = text.ToLowerInvariant().Select(character =>
            character == 'ё' ? 'е' : char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return System.Text.RegularExpressions.Regex.Replace(
            new string(characters), @"\s+", " ").Trim();
    }

    private sealed record PhraseEntry(string Normalized, DateTimeOffset SeenAt);
}
