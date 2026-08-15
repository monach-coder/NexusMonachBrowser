using NexusMonach.Models;

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
        string sourceLanguage, IReadOnlyList<VideoTranslationContextEntry> context,
        DateTimeOffset startedAt, DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var translated = await LocalIntelligenceService.TranslateVideoPhraseAsync(
            transcript, context, cancellationToken, sourceLanguage);
        if (string.IsNullOrWhiteSpace(translated)) return null;

        return new VideoSpeechTranslationText(
            transcript, transcriptWindow, translated, sourceLanguage,
            startedAt, endedAt, context.Count);
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
    private readonly VideoDubbingModeProfile _profile;
    private readonly VideoTranslationContextWindow _context;
    private readonly VideoSourceLanguageTracker _language = new();
    private string _previousWindow = string.Empty;
    private string _pending = string.Empty;
    private string _pendingLanguage = string.Empty;
    private int _pendingParts;
    private DateTimeOffset? _pendingStartedAt;
    private DateTimeOffset _pendingEndedAt;

    public VideoSpeechTranslationContext(VideoTranslationMode mode = VideoTranslationMode.Balanced)
    {
        _profile = VideoDubbingPolicy.ForMode(mode);
        _context = new VideoTranslationContextWindow(_profile);
    }

    public async Task<VideoSpeechTranslationText?> TranslateAsync(
        LiveAudioSegment segment, CancellationToken cancellationToken = default)
    {
        var speech = await NexusFabricRuntime.TranscribeSpeechDetailedAsync(
            segment.Wav, cancellationToken, WhisperLane.Dubbing);
        var transcriptWindow = WhisperService.NormalizeTranscript(speech.Text);
        var sourceLanguage = _language.Observe(transcriptWindow, speech.Language);
        var delta = VideoSpeechTranslationService.RemoveTranscriptOverlap(
            _previousWindow, transcriptWindow);
        _previousWindow = transcriptWindow;
        if (string.IsNullOrWhiteSpace(delta))
            return _pending.Length == 0
                ? null
                : await TranslatePendingAsync(segment, transcriptWindow,
                    sourceLanguage, cancellationToken);

        if (_pending.Length == 0)
        {
            _pendingLanguage = sourceLanguage;
            _pendingStartedAt = segment.CapturedAt;
        }
        var fresh = VideoSpeechTranslationService.RemoveTranscriptOverlap(_pending, delta);
        if (fresh.Length > 0)
        {
            _pending = JoinFragments(_pending, fresh);
            _pendingParts++;
            _pendingEndedAt = segment.EndedAt;
        }
        if (!VideoDubbingPolicy.ShouldFinalizeUtterance(_pending, _pendingParts, _profile))
            return null;

        return await TranslatePendingAsync(segment, transcriptWindow,
            sourceLanguage, cancellationToken);
    }

    private async Task<VideoSpeechTranslationText?> TranslatePendingAsync(
        LiveAudioSegment segment, string transcriptWindow, string fallbackLanguage,
        CancellationToken cancellationToken)
    {
        var complete = _pending;
        var language = string.IsNullOrWhiteSpace(_pendingLanguage)
            ? fallbackLanguage
            : _pendingLanguage;
        var startedAt = _pendingStartedAt ?? segment.CapturedAt;
        var endedAt = _pendingEndedAt > startedAt ? _pendingEndedAt : segment.EndedAt;
        var context = _context.Snapshot(endedAt);
        _pending = string.Empty;
        _pendingLanguage = string.Empty;
        _pendingParts = 0;
        _pendingStartedAt = null;
        _pendingEndedAt = default;
        var translated = await VideoSpeechTranslationService.TranslateToRussianTextAsync(
            segment, complete, transcriptWindow, language, context,
            startedAt, endedAt, cancellationToken);
        if (translated is not null)
        {
            _context.Add(new VideoTranslationContextEntry(
                translated.Transcript, translated.RussianText, translated.SourceLanguage,
                translated.StartedAt, translated.EndedAt));
        }
        return translated;
    }

    internal static bool ShouldFlush(string? text, int fragmentCount)
        => VideoDubbingPolicy.ShouldFinalizeUtterance(text, fragmentCount,
            VideoDubbingPolicy.ForMode(VideoTranslationMode.Balanced));

    internal static string JoinFragments(string? first, string? second)
    {
        first = WhisperService.NormalizeTranscript(first);
        second = WhisperService.NormalizeTranscript(second);
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        return $"{first.TrimEnd()} {second.TrimStart()}";
    }
}

internal sealed record LiveAudioSegment(byte[] Wav, DateTimeOffset CapturedAt,
    TimeSpan Duration = default)
{
    public DateTimeOffset EndedAt => CapturedAt +
        (Duration > TimeSpan.Zero
            ? Duration
            : TimeSpan.FromMilliseconds(VideoDubbingPolicy.SegmentMilliseconds));
}

internal sealed record VideoTranslationContextEntry(
    string Transcript,
    string RussianText,
    string SourceLanguage,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

/// <summary>
/// Whisper language detection is noisy on two-second windows. Two agreeing
/// phrases lock the route for the current video, while a strong script change
/// can still move a genuinely multilingual stream to another model.
/// </summary>
internal sealed class VideoSourceLanguageTracker
{
    private static readonly HashSet<string> EnglishHints = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "for", "from", "in", "is", "it",
        "of", "on", "or", "so", "that", "the", "this", "to", "we", "with", "you", "your"
    };

    private readonly Dictionary<string, int> _votes = new(StringComparer.Ordinal);
    private string _locked = string.Empty;
    private string _conflict = string.Empty;
    private int _conflictVotes;

    public string Observe(string transcript, string? detectedLanguage)
    {
        var candidate = NormalizeLanguage(detectedLanguage);
        var script = DetectStrongScript(transcript);
        if (script.Length > 0) candidate = script;
        else if (LooksPredominantlyEnglish(transcript)) candidate = "en";

        if (_locked.Length > 0)
        {
            if (candidate.Length > 0 && candidate != _locked && script.Length > 0)
            {
                if (_conflict == candidate) _conflictVotes++;
                else
                {
                    _conflict = candidate;
                    _conflictVotes = 1;
                }
                if (_conflictVotes >= 3)
                {
                    _locked = candidate;
                    _conflict = string.Empty;
                    _conflictVotes = 0;
                }
            }
            else
            {
                _conflict = string.Empty;
                _conflictVotes = 0;
            }
            return _locked;
        }

        if (candidate.Length == 0) return detectedLanguage?.Trim() ?? string.Empty;
        _votes[candidate] = _votes.GetValueOrDefault(candidate) + 1;
        var ordered = _votes.OrderByDescending(pair => pair.Value).ToArray();
        if (ordered[0].Value >= 2 &&
            (ordered.Length == 1 || ordered[0].Value > ordered[1].Value))
            _locked = ordered[0].Key;
        return _locked.Length > 0 ? _locked : candidate;
    }

    private static string NormalizeLanguage(string? value)
    {
        value = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.StartsWith("en", StringComparison.Ordinal) || value == "english") return "en";
        if (value.StartsWith("de", StringComparison.Ordinal) || value == "german") return "de";
        if (value.StartsWith("ko", StringComparison.Ordinal) || value == "korean") return "ko";
        if (value.StartsWith("zh", StringComparison.Ordinal) || value == "chinese") return "zh";
        if (value.StartsWith("ja", StringComparison.Ordinal) || value == "japanese") return "ja";
        if (value.StartsWith("ru", StringComparison.Ordinal) || value == "russian") return "ru";
        return value;
    }

    private static string DetectStrongScript(string value)
    {
        var letters = value.Count(char.IsLetter);
        if (letters < 3) return string.Empty;
        if (value.Count(ch => ch is >= '\uAC00' and <= '\uD7AF') >= Math.Max(2, letters / 2)) return "ko";
        if (value.Count(ch => ch is >= '\u3400' and <= '\u9FFF') >= Math.Max(2, letters / 2)) return "zh";
        if (value.Count(ch => ch is >= '\u3040' and <= '\u30FF') >= Math.Max(2, letters / 2)) return "ja";
        if (value.Count(ch => ch is >= '\u0400' and <= '\u04FF') >= Math.Max(2, letters / 2)) return "ru";
        return string.Empty;
    }

    private static bool LooksPredominantlyEnglish(string value)
    {
        var latin = value.Count(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var letters = value.Count(char.IsLetter);
        if (latin < 4 || latin < letters * 0.75) return false;
        var words = System.Text.RegularExpressions.Regex.Matches(
                value.ToLowerInvariant(), @"[a-z]+")
            .Select(match => match.Value).ToArray();
        return words.Count(EnglishHints.Contains) >= 2;
    }
}

internal sealed class VideoTranslationContextWindow(VideoDubbingModeProfile profile)
{
    private readonly Queue<VideoTranslationContextEntry> _entries = new();

    public void Add(VideoTranslationContextEntry entry)
    {
        _entries.Enqueue(entry);
        Trim(entry.EndedAt);
    }

    public IReadOnlyList<VideoTranslationContextEntry> Snapshot(DateTimeOffset now)
    {
        Trim(now);
        return _entries.ToArray();
    }

    private void Trim(DateTimeOffset now)
    {
        var retention = TimeSpan.FromSeconds(profile.ContextSeconds);
        while (_entries.TryPeek(out var oldest) && now - oldest.EndedAt > retention)
            _entries.Dequeue();
        while (_entries.Count > profile.ContextPhrases)
            _entries.Dequeue();
    }
}

internal sealed record VideoSpeechTranslationText(
    string Transcript,
    string TranscriptWindow,
    string RussianText,
    string SourceLanguage,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ContextPhraseCount);

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
        var shorterWordCount = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (shorter.Length >= 4 && shorterWordCount <= 3 &&
            $" {longer} ".Contains($" {shorter} ", StringComparison.Ordinal))
            return true;

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
