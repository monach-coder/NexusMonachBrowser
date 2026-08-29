using System.Windows;
using NexusMonach.Services;

namespace NexusMonach.Views;

/// <summary>
/// «Мой ключ»: публичный ключ личности обмена крупно, с копированием и
/// голосовой сверкой отпечатка. Ключ передаётся любым каналом — он не секрет;
/// отпечаток защищает от подмены ключа по дороге.
/// </summary>
public partial class MyPublicKeyWindow : Window
{
    private readonly string _publicKeyBase64;
    private readonly string _fingerprint;

    public MyPublicKeyWindow()
    {
        InitializeComponent();
        _publicKeyBase64 = Services.Chat.ChatIdentityStore.PublicKeyBase64;
        _fingerprint = Services.Chat.ChatIdentityStore.Fingerprint;
        KeyText.Text = _publicKeyBase64;
        FingerprintText.Text = FormatFingerprint(_fingerprint);
    }

    private static string FormatFingerprint(string fingerprint) =>
        string.Join(' ', Enumerable.Chunk(fingerprint, 4).Select(c => new string(c)));

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_publicKeyBase64);
            VoiceAssistantService.Announce("Ключ скопирован. Передайте его собеседнику.",
                VoiceAnnouncementPriority.Progress);
        }
        catch
        {
            // Буфер занят другим процессом — ключ остаётся выделенным в поле.
            KeyText.SelectAll();
        }
    }

    private void SpeakFingerprint_Click(object sender, RoutedEventArgs e)
    {
        var spoken = string.Join(", ", Enumerable.Chunk(_fingerprint, 4).Select(c => new string(c)));
        VoiceAssistantService.Announce("Отпечаток ключа: " + spoken + ". Сверьте его с собеседником.",
            VoiceAnnouncementPriority.Important);
    }
}
