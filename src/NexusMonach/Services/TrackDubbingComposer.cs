using System.Diagnostics;
using System.Globalization;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Одна готовая реплика дубляжа: точный слот на таймлайне видео и
/// подогнанные по длительности WAV (русская реплика длиннее слота —
/// слегка ускоряется, как в профессиональном одноголосом закадровом).
/// </summary>
internal sealed record TrackDubbedPhrase(
    double StartSeconds,
    double SlotEndSeconds,
    string RussianText,
    IReadOnlyList<string> WavPaths);

/// <summary>
/// Композер синхронного дубляжа по декодированной аудиодорожке: whisper даёт
/// точные таймкоды каждой реплики оригинала, OPUS переводит целыми
/// предложениями с контекстом, Silero озвучивает, а подгон длительности под
/// слот делается локальным ресемплингом (без внешних процессов). Всё на
/// файлах — без страничных захватов и привязок «на глаз», поэтому
/// синхронность получается точной.
/// </summary>
internal static class TrackDubbingComposer
{
    private const int ChunkSeconds = 20;
    private const int ChunkOverlapSeconds = 2;

    /// <summary>
    /// Компонует дубляж окна [fromSeconds, toSeconds] дорожки. Дорожка —
    /// WAV 16к моно; таймкод дорожки совпадает с таймлайном видео.
    /// </summary>
    public static async Task<List<TrackDubbedPhrase>> ComposeAsync(
        byte[] trackWav, double fromSeconds, double toSeconds,
        List<string> allWavs, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var trackSeconds = AudioRateRestore.PcmDurationSeconds(trackWav);
        toSeconds = Math.Min(toSeconds, trackSeconds);
        if (toSeconds - fromSeconds < 1) return [];

        // 1. Реплики оригинала с абсолютными таймкодами: перекрывающиеся
        //    куски + фильтр повторов на стыках.
        var utterances = new List<(double Start, double End, string Text)>();
        var sampleRate = 16_000;
        var chunkStart = Math.Floor(fromSeconds);
        while (chunkStart < toSeconds && !cancellationToken.IsCancellationRequested)
        {
            var chunkEnd = Math.Min(chunkStart + ChunkSeconds, toSeconds + 0.01);
            var chunk = SliceWav(trackWav, chunkStart, chunkEnd);
            if (chunk.Length > 44 + sampleRate) // больше секунды
            {
                progress?.Report($"whisper {chunkStart:F0}–{chunkEnd:F0} с");
                WhisperTranscript? transcript = null;
                try
                {
                    transcript = await WhisperService.TranscribeDetailedAsync(
                        chunk, WhisperLane.Dubbing, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Один неудачный кусок (HTTP 400 на вырожденном звуке и
                    // т.п.) не должен ронять всё окно — просто пропускаем.
                    CrashReportService.RecordNonFatal("video-translation",
                        "compose-whisper-chunk", ex);
                }
                foreach (var segment in transcript?.Segments ?? [])
                {
                    var start = chunkStart + segment.Start;
                    var end = chunkStart + segment.End;
                    if (end <= fromSeconds || start >= toSeconds) continue;
                    // Галлюцинации whisper на тишине: высокая no_speech_prob,
                    // провальная avg_logprob или текст из обрывков знаков.
                    if (segment.NoSpeechProb > 0.6 || segment.AvgLogProb < -1.2) continue;
                    var letters = segment.Text.Count(char.IsLetter);
                    if (letters < 3 || letters < segment.Text.Length * 0.4) continue;
                    // Повтор или нахлёст на стыке кусков.
                    if (utterances.Count > 0)
                    {
                        var last = utterances[^1];
                        if (start < last.End - 1.0) continue;
                        if (Math.Abs(last.Start - start) < ChunkOverlapSeconds + 1.5 &&
                            SameHead(last.Text, segment.Text))
                            continue;
                    }
                    utterances.Add((start, end, segment.Text));
                }
            }
            chunkStart += ChunkSeconds - ChunkOverlapSeconds;
        }
        if (utterances.Count == 0) return [];
        progress?.Report($"реплик оригинала: {utterances.Count}");

        // 2. Перевод целыми предложениями, батчами с контекстом.
        var translations = new string?[utterances.Count];
        for (var offset = 0; offset < utterances.Count; offset += 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = utterances.Skip(offset).Take(12).ToList();
            progress?.Report($"перевод {offset + 1}–{offset + batch.Count}");
            var request = batch.Select((item, index) => new TranslationSegment
            {
                Id = (offset + index).ToString(),
                Text = item.Text,
                Language = "en"
            }).ToArray();
            var translated = await TranslationService.TranslateSegmentsAsync(request, true, cancellationToken);
            foreach (var item in translated)
                if (int.TryParse(item.Id, out var index) &&
                    index >= 0 && index < translations.Length)
                    translations[index] = item.Text;
        }

        // 3. Синтез + подгон под слот.
        var result = new List<TrackDubbedPhrase>();
        for (var i = 0; i < utterances.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var russian = translations[i];
            if (string.IsNullOrWhiteSpace(russian)) continue;
            var (start, end, _) = utterances[i];
            var nextStart = i + 1 < utterances.Count ? utterances[i + 1].Start : end + 2;
            var slot = Math.Max(1.2, Math.Min(nextStart - 0.15, end + 1.5) - start);
            var wavs = new List<string>();
            // Целые предложения без предварительной нарезки: воркер сам
            // делит по 150 знакам на границах фраз, и просодия остаётся
            // цельной. Моя нарезка по 110–130 знакам резала слова и давала
            // заикание на стыках.
            foreach (var piece in VideoDubbingPolicy.SplitTtsText(
                         russian, VideoDubbingPolicy.ForPrecompute()))
            {
                progress?.Report($"озвучка {start:F0} с");
                try
                {
                    var speech = await VideoDubbingVoiceService.PrepareAsync(piece, 0, cancellationToken);
                    wavs.Add(speech.Path);
                    allWavs.Add(speech.Path);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Silero отказывает отдельным кускам («текст слишком
                    // длинный», редкие символы) — реплика озвучивается тем,
                    // что удалось, вместо смерти всего сеанса.
                    CrashReportService.RecordNonFatal("video-translation",
                        "compose-tts-piece", ex);
                }
            }
            if (wavs.Count == 0) continue;
            var fitted = await FitToSlotAsync(wavs, slot, start, cancellationToken);
            if (fitted is null) continue;
            result.Add(new TrackDubbedPhrase(start, start + slot, russian, [fitted]));
            allWavs.Add(fitted);
        }
        progress?.Report($"готово реплик дубляжа: {result.Count}");
        return result;
    }


    /// <summary>
    /// Склеивает части реплики в один WAV и подгоняет под слот: длиннее —
    /// ускоряется до ×1.4 локальным ресемплингом (закадровый приём), короче —
    /// остаётся как есть (пауза до следующей реплики).
    /// </summary>
    private static async Task<string?> FitToSlotAsync(
        IReadOnlyList<string> wavPaths, double slotSeconds, double startSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var merged = wavPaths.Count == 1
                ? await File.ReadAllBytesAsync(wavPaths[0], cancellationToken)
                : await ConcatenateAsync(wavPaths, cancellationToken);
            var duration = AudioRateRestore.PcmDurationSeconds(merged);
            if (duration <= slotSeconds + 0.2 || duration < 0.5)
                return wavPaths.Count == 1
                    ? wavPaths[0]
                    : await WriteTempWavAsync(merged, startSeconds, cancellationToken);
            // Ускорение = растяжение на обратный коэффициент (ресемплинг без
            // внешних процессов и без пользовательского ввода в аргументах).
            // ×1.75 — предел разборчивости; обрезка хвоста — только крайняя
            // мера (потерянная концовка фразы хуже лёгкого вылета за слот).
            var stretch = Math.Max(1 / 1.75, slotSeconds / duration);
            var fitted = AudioRateRestore.RestoreTempo(merged, stretch);
            if (AudioRateRestore.PcmDurationSeconds(fitted) > slotSeconds + 2.0)
                fitted = AudioRateRestore.SliceByTime(fitted, 0, slotSeconds + 2.0);
            return await WriteTempWavAsync(fitted, startSeconds, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation", "fit-slot", ex);
            return null;
        }
    }

    private static async Task<byte[]> ConcatenateAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var buffers = new List<byte[]>();
        foreach (var path in paths)
            buffers.Add(await File.ReadAllBytesAsync(path, cancellationToken));
        var totalSamples = 0;
        foreach (var buffer in buffers)
        {
            if (!AudioRateRestore.TryGetLayout(buffer, out var layout)) return [];
            totalSamples += layout.DataLength / 2 / layout.Channels;
        }
        AudioRateRestore.TryGetLayout(buffers[0], out var first);
        var output = new byte[44 + totalSamples * first.Channels * 2];
        AudioRateRestore.WriteCanonicalHeader(output, totalSamples * first.Channels * 2,
            first.SampleRate, first.Channels);
        var offset = 44;
        foreach (var buffer in buffers)
        {
            AudioRateRestore.TryGetLayout(buffer, out var layout);
            for (var i = 0; i < layout.DataLength; i++)
                output[offset + i] = buffer[layout.DataOffset + i];
            offset += layout.DataLength;
        }
        return output;
    }

    private static async Task<string> WriteTempWavAsync(byte[] wav, double startSeconds,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "nexus-dub-" + startSeconds.ToString("F0", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(path, wav, cancellationToken);
        return path;
    }

    private static byte[] SliceWav(byte[] wav, double fromSeconds, double toSeconds) =>
        AudioRateRestore.SliceByTime(wav, fromSeconds, toSeconds);

    private static bool SameHead(string left, string right)
    {
        var head = Math.Min(16, Math.Min(left.Length, right.Length));
        return head > 0 &&
               left[..head].Equals(right[..head], StringComparison.OrdinalIgnoreCase);
    }
}
