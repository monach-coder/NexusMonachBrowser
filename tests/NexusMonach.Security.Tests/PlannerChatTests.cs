using System.Security.Cryptography;
using NexusMonach.Services.Chat;
using NexusMonach.Services.Planner;
using PlannerTaskStatus = NexusMonach.Services.Planner.TaskStatus;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Планировщик и защищённый обмен: крипто-ядро (ECDH+AES-GCM), кадрирование,
/// инвайт, выжимка маркеров и экспорт .ics. Сетевая часть в юнит-тестах не
/// участвует — только чистые функции.
/// </summary>
public sealed class PlannerChatTests
{
    // ── Крипто ────────────────────────────────────────────────────

    [Fact]
    public void Crypto_RoundTrip_TextSurvives()
    {
        var roomKey = ChatCrypto.GenerateRoomKey();
        var plaintext = "привет, команда"u8.ToArray();
        var framed = ChatCrypto.Encrypt(roomKey, plaintext);
        Assert.NotEqual(plaintext, framed);
        Assert.Equal(plaintext, ChatCrypto.Decrypt(roomKey, framed));
    }

    [Fact]
    public void Crypto_TamperedCiphertext_Fails()
    {
        var roomKey = ChatCrypto.GenerateRoomKey();
        var framed = ChatCrypto.Encrypt(roomKey, "секрет"u8.ToArray());
        framed[^3] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => ChatCrypto.Decrypt(roomKey, framed));
    }

    [Fact]
    public void Crypto_WrongKey_Fails()
    {
        var framed = ChatCrypto.Encrypt(ChatCrypto.GenerateRoomKey(), "секрет"u8.ToArray());
        Assert.ThrowsAny<CryptographicException>(
            () => ChatCrypto.Decrypt(ChatCrypto.GenerateRoomKey(), framed));
    }

    [Fact]
    public void Crypto_RoomKeyWrap_OnlyInviteeCanUnwrap()
    {
        var inviter = new ChatCrypto.Identity();
        var invitee = new ChatCrypto.Identity();
        var outsider = new ChatCrypto.Identity();
        var roomKey = ChatCrypto.GenerateRoomKey();

        var wrapped = inviter.WrapFor(roomKey, invitee.PublicKey);
        // Приглашённый разворачивает парой к своему ключу.
        Assert.Equal(roomKey, invitee.UnwrapFrom(wrapped, inviter.PublicKey));
        // Посторонний — не может.
        Assert.ThrowsAny<CryptographicException>(
            () => outsider.UnwrapFrom(wrapped, inviter.PublicKey));
    }

    [Fact]
    public void Crypto_Fingerprint_StableAndShort()
    {
        var identity = new ChatCrypto.Identity();
        Assert.Equal(ChatCrypto.Fingerprint(identity.PublicKey),
            ChatCrypto.Fingerprint(identity.PublicKey));
        Assert.Equal(16, ChatCrypto.Fingerprint(identity.PublicKey).Length);
    }

    // ── Протокол ──────────────────────────────────────────────────

    [Fact]
    public void Protocol_FrameRoundTrip()
    {
        var payload = new byte[] { 1, 2, 3, 250 };
        var framed = ChatProtocol.Frame(ChatFrameKind.Text, payload);
        Assert.True(ChatProtocol.TryReadHeader(framed, out var kind, out var length));
        Assert.Equal(ChatFrameKind.Text, kind);
        Assert.Equal(payload.Length, length);
        Assert.Equal(payload, framed[ChatProtocol.HeaderBytes..]);
    }

    [Theory]
    [InlineData((ChatFrameKind)99)]
    public void Protocol_RejectsOversized(ChatFrameKind kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChatProtocol.Frame(kind, new byte[ChatProtocol.MaxPayloadBytes + 1]));
    }

    // ── Инвайт ────────────────────────────────────────────────────

    [Fact]
    public void Invite_RoundTrip()
    {
        var inviter = new ChatCrypto.Identity();
        var invitee = new ChatCrypto.Identity();
        var invite = new ChatInvite
        {
            RoomName = "комната тест",
            InviterName = "Саша",
            InviterPublicKey = inviter.PublicKey,
            WrappedRoomKey = inviter.WrapFor(ChatCrypto.GenerateRoomKey(), invitee.PublicKey),
            InviteePublicKey = invitee.PublicKey,
            AnchorEndpoint = "192.168.1.10:9477"
        };
        var parsed = ChatInvite.TryParse(invite.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal("комната тест", parsed!.RoomName);
        Assert.Equal(invite.AnchorEndpoint, parsed.AnchorEndpoint);
    }

    [Theory]
    [InlineData("мусор")]
    [InlineData("{}")]
    public void Invite_RejectsMalformed(string json)
    {
        Assert.Null(ChatInvite.TryParse(json));
    }

    // ── Выжимка маркеров ──────────────────────────────────────────

    [Fact]
    public void Markers_TasksAndFacts_Extracted()
    {
        var markers = ChatGraphBridge.ExtractMarkers(
            "привет\nзадача: подготовить релиз\nфакт: релиз в пятницу\nрешение: ставим в 18:00\nпросто болтовня");
        Assert.Equal(3, markers.Count);
        Assert.Contains(("задача", "подготовить релиз"), markers);
        Assert.Contains(("факт", "релиз в пятницу"), markers);
        Assert.Contains(("решение", "ставим в 18:00"), markers);
    }

    [Fact]
    public void Markers_IgnoreEmptyAndUnmarked()
    {
        Assert.Empty(ChatGraphBridge.ExtractMarkers("задача:  \nфакт:\nобычная строка"));
        Assert.Empty(ChatGraphBridge.ExtractMarkers(""));
    }

    // ── Экспорт .ics ──────────────────────────────────────────────

    [Fact]
    public void Ics_TasksWithDue_BecomeEvents()
    {
        var task = new PlannerTask
        {
            Title = "позвонить в 15:00; офис, переговорка",
            DueUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            Status = PlannerTaskStatus.Open
        };
        var ics = TaskStore.BuildIcs(new[] { task });
        Assert.Contains("BEGIN:VCALENDAR", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260830T120000Z", ics, StringComparison.Ordinal);
        // Экранирование спецсимволов .ics.
        Assert.Contains("переговорка", ics, StringComparison.Ordinal);
        Assert.DoesNotContain(", офис", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Ics_DoneAndUndated_Skipped()
    {
        var ics = TaskStore.BuildIcs(new[]
        {
            new PlannerTask { Title = "без срока" },
            new PlannerTask { Title = "выполнено", DueUtc = DateTimeOffset.UtcNow, Status = PlannerTaskStatus.Done }
        });
        Assert.DoesNotContain("BEGIN:VEVENT", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailto_ContainsTask()
    {
        var task = new PlannerTask { Title = "Релиз 2.9", Notes = "проверить паки" };
        var mailto = TaskStore.BuildMailto(task, "team@example.com");
        Assert.StartsWith("mailto:team%40example.com", mailto, StringComparison.Ordinal);
        Assert.Contains("%D0%A0%D0%B5%D0%BB%D0%B8%D0%B7", mailto, StringComparison.Ordinal); // «Релиз»
    }

    // ── Личность: сохранение и восстановление ────────────────────

    [Fact]
    public void Identity_PemRoundTrip_PreservesKeyAndFingerprint()
    {
        var original = new ChatCrypto.Identity();
        var restored = ChatCrypto.Identity.FromPrivateKeyPem(original.ExportPrivateKeyPem());

        Assert.Equal(original.PublicKey, restored.PublicKey);
        Assert.Equal(ChatCrypto.Fingerprint(original.PublicKey),
            ChatCrypto.Fingerprint(restored.PublicKey));
    }

    [Fact]
    public void Identity_RestoredCanWrapForPeer()
    {
        // Восстановленная из файла личность заворачивает ключ комнаты
        // для собеседника — и собеседник его разворачивает.
        var restored = ChatCrypto.Identity.FromPrivateKeyPem(
            new ChatCrypto.Identity().ExportPrivateKeyPem());
        var peer = new ChatCrypto.Identity();
        var roomKey = ChatCrypto.GenerateRoomKey();

        var wrapped = restored.WrapFor(roomKey, peer.PublicKey);
        Assert.Equal(roomKey, peer.UnwrapFrom(wrapped, restored.PublicKey));
    }

    [Fact]
    public void Identity_RejectsWrongCurve()
    {
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var pem = otherCurve.ExportPkcs8PrivateKeyPem();
        Assert.Throws<CryptographicException>(() =>
            ChatCrypto.Identity.FromPrivateKeyPem(pem));
    }
}
