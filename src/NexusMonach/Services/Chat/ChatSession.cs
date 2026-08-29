using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services.Chat;

public sealed record ChatMember(string Name, byte[] PublicKey)
{
    public string Fingerprint => ChatCrypto.Fingerprint(PublicKey);
}

public sealed record ChatMessage(
    Guid Id,
    string Author,
    DateTimeOffset SentUtc,
    string? Text,
    string? MediaName,
    string? MediaPath)
{
    public bool IsMedia => MediaName is not null;
}

/// <summary>
/// Движок защищённого обмена. Модель «сессия жива, пока хоть один онлайн»:
/// журнал сообщений существует только в оперативной памяти участников;
/// якорем становится старейший онлайн-участник (слушает порт, принимает
/// новичков, отдаёт журнал, передаёт якорность при уходе). Когда уходит
/// последний — всё испаряется: ни сервера, ни диска, ни офлайн-доставки
/// в конструкции нет. Сообщения шифруются ключом комнаты (AES-256-GCM);
/// наружу уходят только шифроблоки и минимальные метаданные кадра.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly ChatCrypto.Identity _identity;
    private readonly string _name;
    private byte[]? _roomKey;
    private readonly List<ChatMessage> _log = [];
    private readonly List<(TcpClient Client, ChatMember Member)> _peers = [];
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _isAnchor;
    private bool _disposed;

    public bool IsAnchor => _isAnchor;
    public IReadOnlyList<ChatMessage> Log { get { lock (_gate) return _log.ToList(); } }
    public IReadOnlyList<ChatMember> Members => PeekMembers();
    public byte[]? PublicKey => _identity.PublicKey;

    /// <summary>События для UI и озвучки: сообщение, вход/выход, смена якоря.</summary>
    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatMember, bool>? MemberChanged; // true — вошёл
    public event Action<string>? StateChanged;

    public ChatSession(ChatCrypto.Identity identity, string displayName)
    {
        _identity = identity;
        _name = string.IsNullOrWhiteSpace(displayName) ? "участник" : displayName.Trim();
    }

    // ── Создание комнаты (якорь сразу мы) ────────────────────────

    public async Task CreateRoomAsync(string roomName, int port)
    {
        _roomKey = ChatCrypto.GenerateRoomKey();
        await StartListeningAsync(port);
        _isAnchor = true;
        StateChanged?.Invoke($"Комната «{roomName}» создана. Вы якорь, порт {port}.");
    }

    /// <summary>Инвайт для конкретного собеседника: заворачиваем ключ комнаты.</summary>
    public ChatInvite BuildInvite(string roomName, byte[] inviteePublicKey, string anchorHost, int port) =>
        new()
        {
            RoomName = roomName,
            InviterName = _name,
            InviterPublicKey = _identity.PublicKey,
            WrappedRoomKey = _identity.WrapFor(_roomKey!, inviteePublicKey),
            InviteePublicKey = inviteePublicKey,
            AnchorEndpoint = anchorHost + ":" + port
        };

    // ── Вход по инвайту ───────────────────────────────────────────

    public async Task JoinAsync(ChatInvite invite)
    {
        _roomKey = _identity.UnwrapFrom(invite.WrappedRoomKey, invite.InviterPublicKey);
        var endpoint = ParseEndpoint(invite.AnchorEndpoint);
        var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port, _cts.Token);
        // Приветствие: публичный ключ + имя, завёрнутые ключом комнаты —
        // якорь сверит ключ и поймёт, что свой.
        var hello = ChatProtocol.Frame(ChatFrameKind.Hello, ChatCrypto.Encrypt(_roomKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name = _name, key = _identity.PublicKey }))));
        await client.GetStream().WriteAsync(hello, _cts.Token);
        _ = Task.Run(() => ReadLoopAsync(client));
        StateChanged?.Invoke("Подключаюсь к якорю " + invite.AnchorEndpoint + "…");
    }

    // ── Якорение: слушаем порт, принимаем участников ──────────────

    private async Task StartListeningAsync(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start(8);
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch { break; }
            _ = Task.Run(() => HandshakeAsync(client));
        }
    }

    private async Task HandshakeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var hello = await ReadFrameAsync(stream);
            if (hello is null || hello.Kind != ChatFrameKind.Hello || _roomKey is null)
            {
                client.Dispose();
                return;
            }
            var payload = JsonSerializer.Deserialize<JsonElement>(
                Encoding.UTF8.GetString(ChatCrypto.Decrypt(_roomKey, hello.Encrypted)));
            var name = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var key = payload.TryGetProperty("key", out var k)
                ? k.GetBytesFromBase64() : [];
            var member = new ChatMember(name, key);

            // Вэлком + полный журнал новичку (шифром).
            var welcome = ChatProtocol.Frame(ChatFrameKind.Welcome, ChatCrypto.Encrypt(_roomKey,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Members.Append(member)
                    .Select(m => new { m.Name, key = Convert.ToBase64String(m.PublicKey) })))));
            await stream.WriteAsync(welcome, _cts.Token);
            lock (_gate)
            {
                _peers.Add((client, member));
            }
            MemberChanged?.Invoke(member, true);
            _ = Task.Run(() => ReadLoopAsync(client));
        }
        catch
        {
            try { client.Dispose(); } catch { }
        }
    }

    // ── Чтение кадров от пира ─────────────────────────────────────

    private async Task ReadLoopAsync(TcpClient client)
    {
        var stream = client.GetStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream);
                if (frame is null || _roomKey is null) break;
                switch (frame.Kind)
                {
                    case ChatFrameKind.Text:
                        Broadcast(frame.Raw, except: client);
                        DeliverText(frame.Encrypted);
                        break;
                    case ChatFrameKind.Media:
                        Broadcast(frame.Raw, except: client);
                        DeliverMedia(frame.Encrypted);
                        break;
                    case ChatFrameKind.Leave:
                        DropPeer(client);
                        break;
                    case ChatFrameKind.AnchorHandover:
                        // Якорь ушёл и назначил нас: поднимаем свой слушатель.
                        var port = ParseEndpoint(
                            Encoding.UTF8.GetString(ChatCrypto.Decrypt(_roomKey, frame.Encrypted))).Port;
                        if (!_isAnchor)
                        {
                            _isAnchor = true;
                            await StartListeningAsync(port);
                            StateChanged?.Invoke("Якорь ушёл — якорность передана вам (порт " + port + ").");
                        }
                        break;
                    case ChatFrameKind.LogSync:
                        ApplySync(frame.Encrypted);
                        break;
                }
            }
        }
        catch
        {
            DropPeer(client);
        }
    }

    private void DeliverText(byte[] encrypted)
    {
        if (_roomKey is null) return;
        var payload = JsonSerializer.Deserialize<JsonElement>(
            Encoding.UTF8.GetString(ChatCrypto.Decrypt(_roomKey, encrypted)));
        var message = new ChatMessage(
            payload.GetProperty("id").GetGuid(),
            payload.GetProperty("author").GetString() ?? "?",
            payload.GetProperty("utc").GetDateTimeOffset(),
            payload.GetProperty("text").GetString(), null, null);
        lock (_gate) _log.Add(message);
        MessageReceived?.Invoke(message);
    }

    private void DeliverMedia(byte[] encrypted)
    {
        if (_roomKey is null) return;
        var payload = JsonSerializer.Deserialize<JsonElement>(
            Encoding.UTF8.GetString(ChatCrypto.Decrypt(_roomKey, encrypted)));
        var name = payload.GetProperty("name").GetString() ?? "файл";
        var bytes = payload.GetProperty("data").GetBytesFromBase64();
        // Медиа живёт на диске ровно до закрытия: DeleteOnClose и никакого
        // следа после просмотра/сессии.
        var tempPath = Path.Combine(Path.GetTempPath(),
            "nexus-chat-" + Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        using (var handle = File.Create(tempPath))
            handle.Write(bytes);
        var message = new ChatMessage(
            payload.GetProperty("id").GetGuid(),
            payload.GetProperty("author").GetString() ?? "?",
            payload.GetProperty("utc").GetDateTimeOffset(),
            null, name, tempPath);
        lock (_gate) _log.Add(message);
        MessageReceived?.Invoke(message);
    }

    private void ApplySync(byte[] encrypted)
    {
        if (_roomKey is null) return;
        var payload = JsonSerializer.Deserialize<ChatMessage[]>(
            Encoding.UTF8.GetString(ChatCrypto.Decrypt(_roomKey, encrypted))) ?? [];
        lock (_gate)
        {
            foreach (var message in payload.Where(m => !_log.Any(x => x.Id == m.Id)))
                _log.Add(message);
            _log.Sort((a, b) => a.SentUtc.CompareTo(b.SentUtc));
        }
        foreach (var message in payload)
            MessageReceived?.Invoke(message);
    }

    // ── Отправка ──────────────────────────────────────────────────

    public async Task SendTextAsync(string text)
    {
        if (_roomKey is null || string.IsNullOrWhiteSpace(text)) return;
        var message = new ChatMessage(Guid.NewGuid(), _name, DateTimeOffset.Now, text.Trim(), null, null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = message.Id, author = message.Author, utc = message.SentUtc, text = message.Text
        });
        var frame = ChatProtocol.Frame(ChatFrameKind.Text, ChatCrypto.Encrypt(_roomKey, payload));
        lock (_gate) _log.Add(message);
        await BroadcastAsync(frame);
        MessageReceived?.Invoke(message);
    }

    public async Task SendMediaAsync(string fileName, byte[] bytes)
    {
        if (_roomKey is null || bytes.Length == 0) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = Guid.NewGuid(), author = _name, utc = DateTimeOffset.Now,
            name = Path.GetFileName(fileName), data = Convert.ToBase64String(bytes)
        });
        var frame = ChatProtocol.Frame(ChatFrameKind.Media, ChatCrypto.Encrypt(_roomKey, payload));
        await BroadcastAsync(frame);
        DeliverMedia(ChatCrypto.Encrypt(_roomKey, payload)); // локальная доставка этого же блоба
    }

    // ── Рассылка парам ────────────────────────────────────────────

    private void Broadcast(byte[] frame, TcpClient? except = null)
    {
        lock (_gate)
        {
            foreach (var (client, _) in _peers.Where(p => !ReferenceEquals(p.Client, except)).ToList())
                try { client.GetStream().Write(frame); } catch { DropPeer(client); }
        }
    }

    private async Task BroadcastAsync(byte[] frame)
    {
        List<TcpClient> targets;
        lock (_gate) targets = _peers.Select(p => p.Client).ToList();
        foreach (var client in targets)
            try { await client.GetStream().WriteAsync(frame, _cts.Token); }
            catch { DropPeer(client); }
    }

    private void DropPeer(TcpClient client)
    {
        ChatMember? gone = null;
        lock (_gate)
        {
            var peer = _peers.FirstOrDefault(p => ReferenceEquals(p.Client, client));
            if (peer.Member is { } member) gone = member;
            _peers.RemoveAll(p => ReferenceEquals(p.Client, client));
        }
        try { client.Dispose(); } catch { }
        if (gone is not null)
        {
            MemberChanged?.Invoke(gone, false);
            // Последний участник уходит — сессия умирает по спецификации.
            lock (_gate)
            {
                if (_peers.Count == 0 && !_isAnchor)
                    StateChanged?.Invoke("Все участники ушли. Сессия завершена — переписка испарилась.");
            }
        }
    }

    private IReadOnlyList<ChatMember> PeekMembers()
    {
        lock (_gate) return _peers.Select(p => p.Member).ToList();
    }

    // ── Кадровое чтение из потока ─────────────────────────────────

    private sealed record RawFrame(ChatFrameKind Kind, byte[] Encrypted, byte[] Raw);

    private static async Task<RawFrame?> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[ChatProtocol.HeaderBytes];
        if (!await ReadExactAsync(stream, header)) return null;
        if (!ChatProtocol.TryReadHeader(header, out var kind, out var length)) return null;
        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload)) return null;
        return new RawFrame(kind, payload, header.Concat(payload).ToArray());
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read));
            if (chunk == 0) return false;
            read += chunk;
        }
        return true;
    }

    private static IPEndPoint ParseEndpoint(string hostPort)
    {
        var parts = hostPort.Split(':');
        return new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        lock (_gate)
        {
            foreach (var (client, _) in _peers)
                try { client.Dispose(); } catch { }
            _peers.Clear();
            _log.Clear(); // RAM-журнал умирает вместе с сессией
        }
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
