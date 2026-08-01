using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VoiceCommandRouterTests
{
    [Theory]
    [InlineData("переведи страницу", (int)VoiceCommandKind.TranslatePage, "")]
    [InlineData("переведи видео", (int)VoiceCommandKind.TranslateVideo, "")]
    [InlineData("открой гардиан", (int)VoiceCommandKind.OpenGuardian, "")]
    [InlineData("новая вкладка", (int)VoiceCommandKind.NewTab, "")]
    [InlineData("найди локальный приватный браузер", (int)VoiceCommandKind.Search, "локальный приватный браузер")]
    [InlineData("найди в сети новости технологий", (int)VoiceCommandKind.Search, "новости технологий")]
    public void PushToTalk_ParsesSupportedCommands(string transcript, int kindValue, string argument)
    {
        var command = VoiceCommandRouter.Parse(transcript, requireWakeWord: false);

        Assert.Equal((VoiceCommandKind)kindValue, command.Kind);
        Assert.Equal(argument, command.Argument);
    }

    [Fact]
    public void HandsFree_RequiresWakeWord()
    {
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("переведи страницу", requireWakeWord: true).Kind);
        Assert.Equal(VoiceCommandKind.TranslatePage,
            VoiceCommandRouter.Parse("Нексус, переведи страницу", requireWakeWord: true).Kind);
    }

    [Fact]
    public void UnknownSpeech_DoesNotBecomeSearchAutomatically()
    {
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("мой пароль сегодня прекрасный", requireWakeWord: false).Kind);
    }
}
