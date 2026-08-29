using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services.Chat;

/// <summary>
/// Хранилище личности защищённого обмена: одна пара ключей на браузер,
/// рождается при первом обращении и переживает перезапуски. Приватная часть
/// лежит на диске только в DPAPI-обёртке текущего пользователя Windows —
/// файл, скопированный с диска, бесполезен на чужой машине и для чужого
/// процесса другого пользователя. Переписка по-прежнему RAM-only: личность
/// постоянна, сообщения исчезают с сессией.
/// </summary>
public static class ChatIdentityStore
{
    private static readonly object Sync = new();
    private static ChatCrypto.Identity? _identity;
    // Привязка обёртки к назначению: байты энтропии DPAPI.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("nexus-monach-chat-identity-v1");

    public static ChatCrypto.Identity Identity
    {
        get
        {
            lock (Sync)
            {
                _identity ??= LoadOrCreate();
                return _identity;
            }
        }
    }

    public static byte[] PublicKey => Identity.PublicKey;

    public static string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    public static string Fingerprint => ChatCrypto.Fingerprint(PublicKey);

    private static ChatCrypto.Identity LoadOrCreate()
    {
        try
        {
            if (File.Exists(AppPaths.ChatIdentityFile))
            {
                var dto = JsonSerializer.Deserialize<IdentityFile>(
                    File.ReadAllText(AppPaths.ChatIdentityFile));
                if (dto?.SchemaVersion == 1 &&
                    !string.IsNullOrWhiteSpace(dto.ProtectedPrivateKey) &&
                    !string.IsNullOrWhiteSpace(dto.PublicKeyBase64))
                {
                    var pem = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        Convert.FromBase64String(dto.ProtectedPrivateKey),
                        Entropy, DataProtectionScope.CurrentUser));
                    var restored = ChatCrypto.Identity.FromPrivateKeyPem(pem);
                    // Файл подменён или бит: публичный ключ обязан совпадать.
                    if (Convert.ToBase64String(restored.PublicKey)
                        .Equals(dto.PublicKeyBase64, StringComparison.Ordinal))
                        return restored;
                }
            }
        }
        catch
        {
            // Повреждённая или чужая личность — рождаём новую. Старые комнаты
            // всё равно исчезают вместе с сессией, потерь нет.
        }
        return CreateAndSave();
    }

    private static ChatCrypto.Identity CreateAndSave()
    {
        var fresh = new ChatCrypto.Identity();
        try
        {
            var dto = new IdentityFile
            {
                SchemaVersion = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                PublicKeyBase64 = Convert.ToBase64String(fresh.PublicKey),
                ProtectedPrivateKey = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(fresh.ExportPrivateKeyPem()),
                    Entropy, DataProtectionScope.CurrentUser))
            };
            File.WriteAllText(AppPaths.ChatIdentityFile,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Нет диска — остаёмся RAM-only на эту сессию.
        }
        return fresh;
    }

    private sealed record IdentityFile
    {
        public int SchemaVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public string PublicKeyBase64 { get; init; } = string.Empty;
        public string ProtectedPrivateKey { get; init; } = string.Empty;
    }
}
