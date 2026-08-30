using System.Security.Cryptography;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Ротация приватных мостов: пользователь один раз заполняет пул своими
/// webtunnel/obfs4-мостами (по одному в строке), и каждая сессия берёт
/// случайной рукой один мост. Публичные списки не используются в принципе:
/// выложенное в открытый доступ попадает в блок-листы первым. Криптографический
/// генератор выбора — предсказать следующий мост по предыдущему нельзя.
/// </summary>
public static class BridgeRotator
{
    /// <summary>
    /// Случайная непустая строка пула или null, если пуля нет/пуст.
    /// Чистая функция выбора для тестов — случайность черезcrypto-рандом.
    /// </summary>
    public static string? PickSessionBridge(string? pool) =>
        PickSessionBridge(pool, RandomNumberGenerator.GetInt32);

    internal static string? PickSessionBridge(string? pool, Func<int, int> next)
    {
        if (string.IsNullOrWhiteSpace(pool)) return null;
        var lines = pool
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (lines.Count == 0) return null;
        return lines[next(lines.Count)];
    }
}
