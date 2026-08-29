using System.Security.Cryptography;

namespace NexusMonach.Services.Chat;

/// <summary>
/// Крипто-ядро защищённого обмена: личная пара ключей ECDH P-256
/// (приватная — только в памяти процесса), выработка общего секрета,
/// заворачивание ключа комнаты на публичный ключ приглашённого и
/// шифрование AES-256-GCM. Серверов и хранения ключей нет в принципе:
/// пока участники онлайн — сессия жива в их оперативной памяти.
/// </summary>
public static class ChatCrypto
{
    public const int RoomKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// Личная пара ключей участника. Приватная часть не покидает браузер
    /// владельца: в памяти — процесс, на диске — только DPAPI-зашифрованный
    /// файл личности (см. ChatIdentityStore).
    /// </summary>
    public sealed class Identity
    {
        private readonly ECDiffieHellman _dh;
        public byte[] PublicKey { get; }

        public Identity()
        {
            _dh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var point = _dh.PublicKey.ExportParameters().Q;
            PublicKey = (point.X ?? []).Concat(point.Y ?? []).ToArray();
        }

        private Identity(ECDiffieHellman existing)
        {
            _dh = existing;
            var point = _dh.PublicKey.ExportParameters().Q;
            PublicKey = (point.X ?? []).Concat(point.Y ?? []).ToArray();
        }

        /// <summary>Приватная часть в PKCS#8 PEM — для DPAPI-хранилища личности.</summary>
        public string ExportPrivateKeyPem() => _dh.ExportPkcs8PrivateKeyPem();

        /// <summary>Восстановление личности из PKCS#8 PEM. Кривая — строго P-256.</summary>
        public static Identity FromPrivateKeyPem(string pem)
        {
            using var parser = ECDsa.Create();
            parser.ImportFromPem(pem);
            var parameters = parser.ExportParameters(includePrivateParameters: true);
            // ECCurve.Equals ненадёжен для именованных кривых — сверяем OID.
            if (parameters.Curve.Oid?.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Личность чата использует неожиданную кривую.");
            return new Identity(ECDiffieHellman.Create(parameters));
        }

        /// <summary>Общий секрет с собеседником (ECDH) → ключ AES-256.</summary>
        public byte[] DeriveSharedKey(byte[] peerPublicKey)
        {
            if (peerPublicKey is null || peerPublicKey.Length != 64)
                throw new CryptographicException("Некорректный публичный ключ собеседника.");
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peerPublicKey[..32], Y = peerPublicKey[32..] }
            };
            using var peer = ECDiffieHellman.Create(parameters);
            var secret = _dh.DeriveRawSecretAgreement(peer.PublicKey);
            // HKDF-упрощение: секрет 32 байта уже — материал ключа комнаты.
            return SHA256.HashData(secret);
        }

        /// <summary>
        /// Заворачивает ключ комнаты на публичный ключ приглашённого:
        /// ECDH(мой приватный, его публичный) → AES-GCM обёртка. Открыть
        /// сможет только владелец парного приватного ключа.
        /// </summary>
        public byte[] WrapFor(byte[] roomKey, byte[] inviteePublicKey) =>
            Encrypt(DeriveSharedKey(inviteePublicKey), roomKey);

        /// <summary>Разворачивает ключ комнаты, завёрнутый пригласившим.</summary>
        public byte[] UnwrapFrom(byte[] wrapped, byte[] inviterPublicKey) =>
            Decrypt(DeriveSharedKey(inviterPublicKey), wrapped);
    }

    /// <summary>Генерирует новый ключ комнаты.</summary>
    public static byte[] GenerateRoomKey() => RandomNumberGenerator.GetBytes(RoomKeyBytes);

    /// <summary>Шифрует payload ключом комнаты: nonce || ciphertext || tag.</summary>
    public static byte[] Encrypt(byte[] roomKey, byte[] plaintext, byte[]? associated = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(roomKey, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, associated);
        return nonce.Concat(cipher).Concat(tag).ToArray();
    }

    /// <summary>Расшифровывает payload ключом комнаты; порча — исключение.</summary>
    public static byte[] Decrypt(byte[] roomKey, byte[] framed, byte[]? associated = null)
    {
        if (framed.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Шифроблок слишком короткий.");
        var nonce = framed[..NonceBytes];
        var tag = framed[^TagBytes..];
        var cipher = framed[NonceBytes..^TagBytes];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(roomKey, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain, associated);
        return plain;
    }

    /// <summary>Подпись-метка участника: публичный ключ + отпечаток для отображения.</summary>
    public static string Fingerprint(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}
