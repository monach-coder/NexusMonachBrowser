using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VoiceProfileSelectorTests
{
    [Fact]
    public void RussianFemaleVoice_HasHighestPriority()
    {
        NexusVoiceCandidate[] voices =
        [
            new("Russian male", "ru-RU", NexusVoiceGender.Male),
            new("English female", "en-US", NexusVoiceGender.Female),
            new("Russian female", "ru-RU", NexusVoiceGender.Female)
        ];

        Assert.Equal(2, VoiceProfileSelector.SelectPreferredIndex(voices));
    }

    [Fact]
    public void AnyFemaleVoice_IsPreferredWhenRussianFemaleIsMissing()
    {
        NexusVoiceCandidate[] voices =
        [
            new("Russian male", "ru-RU", NexusVoiceGender.Male),
            new("English female", "en-US", NexusVoiceGender.Female)
        ];

        Assert.Equal(1, VoiceProfileSelector.SelectPreferredIndex(voices));
    }

    [Fact]
    public void RussianVoice_IsFallbackWhenNoFemaleVoiceExists()
    {
        NexusVoiceCandidate[] voices =
        [
            new("English male", "en-US", NexusVoiceGender.Male),
            new("Russian male", "ru-RU", NexusVoiceGender.Male)
        ];

        Assert.Equal(1, VoiceProfileSelector.SelectPreferredIndex(voices));
    }
}
