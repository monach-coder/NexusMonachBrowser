using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Мгновенный офлайн-слой для часто повторяющихся элементов веб-интерфейса.
/// Он намеренно переводит только целые известные фразы: пословная подстановка
/// без контекста ухудшает статьи и может менять смысл юридического текста.
/// </summary>
public static class LocalTranslationDictionary
{
    private const int MaximumSessionEntries = 4096;
    private static readonly ConcurrentDictionary<string, string> SessionPhrases =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> Phrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sign in"] = "Войти", ["log in"] = "Войти", ["sign out"] = "Выйти",
            ["log out"] = "Выйти", ["create account"] = "Создать аккаунт",
            ["continue"] = "Продолжить", ["next"] = "Далее", ["back"] = "Назад",
            ["cancel"] = "Отмена", ["close"] = "Закрыть", ["save"] = "Сохранить",
            ["apply"] = "Применить", ["confirm"] = "Подтвердить", ["delete"] = "Удалить",
            ["edit"] = "Изменить", ["settings"] = "Настройки", ["privacy"] = "Конфиденциальность",
            ["security"] = "Безопасность", ["help"] = "Справка", ["learn more"] = "Подробнее",
            ["read more"] = "Читать далее", ["show more"] = "Показать ещё",
            ["show less"] = "Показать меньше", ["search"] = "Поиск", ["menu"] = "Меню",
            ["home"] = "Главная", ["download"] = "Скачать", ["downloads"] = "Загрузки",
            ["upload"] = "Загрузить", ["share"] = "Поделиться", ["copy"] = "Копировать",
            ["language"] = "Язык", ["account"] = "Аккаунт", ["profile"] = "Профиль",
            ["notifications"] = "Уведомления", ["accept"] = "Принять", ["decline"] = "Отклонить",
            ["accept all"] = "Принять все", ["reject all"] = "Отклонить все",
            ["manage options"] = "Настроить", ["cookie settings"] = "Настройки cookie",
            ["terms of service"] = "Условия использования", ["privacy policy"] = "Политика конфиденциальности",
            ["add to cart"] = "Добавить в корзину", ["buy now"] = "Купить сейчас",
            ["shopping cart"] = "Корзина", ["cart"] = "Корзина", ["checkout"] = "Оформить заказ",
            ["price"] = "Цена", ["rating"] = "Рейтинг", ["reviews"] = "Отзывы",
            ["in stock"] = "В наличии", ["out of stock"] = "Нет в наличии",
            ["free delivery"] = "Бесплатная доставка", ["delivery"] = "Доставка",
            ["previous"] = "Назад", ["today"] = "Сегодня", ["yesterday"] = "Вчера",
            ["最新"] = "Новое", ["検索"] = "Поиск", ["設定"] = "Настройки", ["ログイン"] = "Войти",
            ["次へ"] = "Далее", ["戻る"] = "Назад", ["閉じる"] = "Закрыть", ["保存"] = "Сохранить",
            ["搜索"] = "Поиск", ["设置"] = "Настройки", ["登录"] = "Войти", ["退出"] = "Выйти",
            ["下一步"] = "Далее", ["返回"] = "Назад", ["关闭"] = "Закрыть", ["保存更改"] = "Сохранить изменения",
            ["connexion"] = "Войти", ["paramètres"] = "Настройки", ["suivant"] = "Далее",
            ["zurück"] = "Назад", ["anmelden"] = "Войти", ["einstellungen"] = "Настройки",
            ["weiter"] = "Далее", ["schließen"] = "Закрыть"
        };

    public static IReadOnlyList<TranslationSegment> TranslateKnown(IReadOnlyList<TranslationSegment> segments) =>
        segments.Select(segment => TryTranslate(segment.Text, out var text)
                ? new TranslationSegment { Id = segment.Id, Text = text }
                : null)
            .Where(segment => segment is not null)
            .Cast<TranslationSegment>()
            .ToArray();

    public static bool TryTranslate(string value, out string translated)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        var punctuation = normalized.TrimEnd('.', ':', '!', '?', '…');
        if (Phrases.TryGetValue(punctuation, out var builtIn))
        {
            translated = builtIn + normalized[punctuation.Length..];
            return !translated.Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        if (SessionPhrases.TryGetValue(normalized, out var remembered))
        {
            translated = remembered;
            return !translated.Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        translated = string.Empty;
        return false;
    }

    /// <summary>
    /// Remembers complete translated phrases only for the current process. Page
    /// text is deliberately never persisted to disk: the cache is a speed layer,
    /// not a new browsing-history store.
    /// </summary>
    public static void Remember(string source, string translation)
    {
        source = Regex.Replace(source ?? string.Empty, @"\s+", " ").Trim();
        translation = Regex.Replace(translation ?? string.Empty, @"\s+", " ").Trim();
        if (source.Length < 2 || translation.Length < 1 || source.Length > 1200 ||
            source.Equals(translation, StringComparison.OrdinalIgnoreCase)) return;
        if (SessionPhrases.Count >= MaximumSessionEntries)
        {
            var oldest = SessionPhrases.Keys.FirstOrDefault();
            if (oldest is not null) SessionPhrases.TryRemove(oldest, out _);
        }
        SessionPhrases[source] = translation;
    }
}
