using System.Text.Json;

namespace NexusMonach.Services.Chat;

public enum ChatFrameKind : byte
{
    Hello = 1,          // публичный ключ участника + имя
    Welcome = 2,        // якорь принимает: список участников
    LogSync = 3,        // якорь отдаёт канонический журнал новенькому
    Text = 4,           // текстовое сообщение (шифроблоб)
    Media = 5,          // медиа-файл: имя + тип + шифроблоб
    Extract = 6,        // извлечённый факт/задача (шифроблоб), для графа у всех
    AnchorHandover = 7, // якорь уходит, назначает следующего
    Leave = 8           // участник прощается
}

/// <summary>
/// Кадр протокола: тип + длина + payload. Текст описания кадра — только
/// метаданные (тип/размер); содержимое сообщений ходит шифроблобами
/// под ключ комнаты. Кадрирование — 1 байт типа, 4 байта длины (big-endian),
/// затем payload.
/// </summary>
public static class ChatProtocol
{
    public const int HeaderBytes = 5;
    public const int MaxPayloadBytes = 32 * 1024 * 1024;

    public static byte[] Frame(ChatFrameKind kind, byte[] payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "Кадр превышает лимит протокола.");
        var header = new byte[HeaderBytes];
        header[0] = (byte)kind;
        var length = BitConverter.GetBytes(payload.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(length);
        Array.Copy(length, 0, header, 1, 4);
        return header.Concat(payload).ToArray();
    }

    /// <summary>Разбирает заголовок кадра из буфера; успех — и длина payload.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out ChatFrameKind kind, out int payloadLength)
    {
        kind = 0;
        payloadLength = 0;
        if (buffer.Length < HeaderBytes) return false;
        kind = (ChatFrameKind)buffer[0];
        var length = buffer[1..5].ToArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(length);
        payloadLength = BitConverter.ToInt32(length);
        return payloadLength is >= 0 and <= MaxPayloadBytes;
    }
}

/// <summary>
/// Инвайт: единственный способ попасть в комнату. Файл передаётся любым
/// каналом; внутри — публичный ключ комнаты-создателя, завёрнутый на ключ
/// приглашённого ключ комнаты, адрес якоря и отпечатки для сверки.
/// </summary>
public sealed record ChatInvite
{
    public int SchemaVersion { get; init; } = 1;
    public required string RoomName { get; init; }
    public required string InviterName { get; init; }
    /// <summary>Публичный ключ пригласившего (для разворачивания ключа комнаты).</summary>
    public required byte[] InviterPublicKey { get; init; }
    /// <summary>Ключ комнаты, завёрнутый на ключ приглашённого.</summary>
    public required byte[] WrappedRoomKey { get; init; }
    /// <summary>Публичный ключ приглашённого — подтверждение адресата.</summary>
    public required byte[] InviteePublicKey { get; init; }
    /// <summary>Адрес якоря: host:port.</summary>
    public required string AnchorEndpoint { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = true });

    public static ChatInvite? TryParse(string json)
    {
        try
        {
            var invite = JsonSerializer.Deserialize<ChatInvite>(json);
            return invite is { SchemaVersion: 1 } &&
                   invite.InviterPublicKey.Length == 64 &&
                   invite.InviteePublicKey.Length == 64 &&
                   invite.WrappedRoomKey.Length > 0 &&
                   invite.AnchorEndpoint.Contains(':')
                ? invite : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
