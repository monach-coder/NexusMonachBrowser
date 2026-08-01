namespace NexusMonach.Services;

internal enum NexusVoiceGender
{
    Unknown,
    Female,
    Male
}

internal sealed record NexusVoiceCandidate(string Name, string Culture, NexusVoiceGender Gender);

internal static class VoiceProfileSelector
{
    public static int SelectPreferredIndex(IReadOnlyList<NexusVoiceCandidate> voices)
    {
        if (voices.Count == 0) return -1;
        var index = Find(voices, voice => IsRussian(voice) && voice.Gender == NexusVoiceGender.Female);
        if (index >= 0) return index;
        index = Find(voices, voice => voice.Gender == NexusVoiceGender.Female);
        if (index >= 0) return index;
        index = Find(voices, IsRussian);
        return index >= 0 ? index : 0;
    }

    public static bool IsRussian(NexusVoiceCandidate voice) =>
        voice.Culture.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    private static int Find(IReadOnlyList<NexusVoiceCandidate> voices, Func<NexusVoiceCandidate, bool> predicate)
    {
        for (var index = 0; index < voices.Count; index++)
            if (predicate(voices[index])) return index;
        return -1;
    }
}
