using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VoicePrivacyTests
{
    [Fact]
    public void PrivateWindow_IsNeverSpoken()
    {
        Assert.False(VoiceAssistantService.ShouldSpeak(
            VoiceAssistantMode.Assistant, VoiceAnnouncementPriority.Critical, isPrivateWindow: true));
    }

    [Fact]
    public void ImportantOnly_SuppressesProgressButKeepsSecurityWarnings()
    {
        Assert.False(VoiceAssistantService.ShouldSpeak(
            VoiceAssistantMode.ImportantOnly, VoiceAnnouncementPriority.Progress, isPrivateWindow: false));
        Assert.True(VoiceAssistantService.ShouldSpeak(
            VoiceAssistantMode.ImportantOnly, VoiceAnnouncementPriority.Critical, isPrivateWindow: false));
    }

    [Fact]
    public void SpeechSanitizer_RemovesUrlsEmailsTokensAndControlCharacters()
    {
        var sanitized = VoiceAssistantService.SanitizeForSpeech(
            "Готово https://secret.example/a user@example.org token=abc123\r\nСледующий этап");

        Assert.DoesNotContain("secret.example", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user@example", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.Contains("скрыто", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpeechText_IsStrictlyBounded()
    {
        Assert.True(VoiceAssistantService.SanitizeForSpeech(new string('я', 1000)).Length <= 360);
    }
}
