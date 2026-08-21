using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusMonach.Services;

/// <summary>
/// Converts written Russian text into a stable spoken form before it reaches
/// any TTS engine. The implementation is deterministic, local and shared by
/// the neural and Windows fallback voices.
/// </summary>
internal static partial class RussianSpeechTextNormalizer
{
    private const int MaximumDictionaryBytes = 128 * 1024;
    private static readonly object DictionarySync = new();
    private static readonly Dictionary<string, string> BuiltInPronunciations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nexus Monach"] = "Нексус Монах",
        ["Nexus"] = "Нексус",
        ["WebView2"] = "веб-вью два",
        ["Chromium"] = "Хромиум",
        ["DevTools"] = "девтулз",
        ["WebRTC"] = "веб эр-ти-си",
        ["JavaScript"] = "джаваскрипт",
        ["GitHub"] = "гитхаб",
        ["Windows"] = "Виндоус",
        ["AI"] = "эй-ай",
        ["TTS"] = "ти-ти-эс",
        ["URL"] = "ю-ар-эл",
        ["VPN"] = "ви-пи-эн",
        ["DNS"] = "ди-эн-эс",
        ["HTTPS"] = "эйч-ти-ти-пи-эс",
        ["HTTP"] = "эйч-ти-ти-пи",
        ["CPU"] = "си-пи-ю",
        ["GPU"] = "джи-пи-ю"
    };

    private static string _cachedDictionaryPath = string.Empty;
    private static DateTime _cachedDictionaryWriteUtc;
    private static IReadOnlyDictionary<string, string> _cachedCustomPronunciations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] MasculineOnes =
        ["", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] FeminineOnes =
        ["", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] Teens =
        ["десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] Tens =
        ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Hundreds =
        ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];
    private static readonly string[] MonthGenitive =
        ["", "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    private static readonly string[] DayOrdinalGenitive =
    [
        "", "первого", "второго", "третьего", "четвёртого", "пятого", "шестого", "седьмого",
        "восьмого", "девятого", "десятого", "одиннадцатого", "двенадцатого", "тринадцатого",
        "четырнадцатого", "пятнадцатого", "шестнадцатого", "семнадцатого", "восемнадцатого",
        "девятнадцатого", "двадцатого", "двадцать первого", "двадцать второго", "двадцать третьего",
        "двадцать четвёртого", "двадцать пятого", "двадцать шестого", "двадцать седьмого",
        "двадцать восьмого", "двадцать девятого", "тридцатого", "тридцать первого"
    ];

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Normalize(NormalizationForm.FormKC);
        text = ApplyPronunciationDictionary(text);
        text = DatePattern().Replace(text, ReplaceDate);
        text = TimePattern().Replace(text, ReplaceTime);
        text = NumberSignPattern().Replace(text, match => "номер " + IntegerToWords(ParseInteger(match.Groups[1].Value)));
        text = TemperaturePattern().Replace(text, ReplaceTemperature);
        text = DataUnitPattern().Replace(text, ReplaceDataUnit);
        text = PercentPattern().Replace(text, ReplacePercent);
        text = RublePattern().Replace(text, ReplaceRubles);
        text = DollarPattern().Replace(text, ReplaceDollars);
        text = EuroPattern().Replace(text, ReplaceEuros);
        text = DecimalPattern().Replace(text, match => DecimalToWords(match.Groups[1].Value, match.Groups[2].Value));
        text = IntegerPattern().Replace(text, match => IntegerToWords(ParseInteger(match.Value)));
        // Транслитерация идёт после числовых подстановок: FormKC превращает
        // «№» в латинское «No», а единицы вроде «°C» должны сначала попасть
        // в свои шаблоны — иначе «№7» станет «но7».
        text = TransliterateEnglish(text);
        text = text.Replace("&", " и ", StringComparison.Ordinal);
        text = RepeatedPunctuationPattern().Replace(text, match => match.Value[..1]);
        return SpacePattern().Replace(text, " ").Trim();
    }

    // Произношение частых английских слов, которые нельзя угадать правилами:
    // поразрядная транслитерация делает из них нечто нечленораздельное.
    private static readonly Dictionary<string, string> EnglishPronunciations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = "гугл", ["youtube"] = "ютуб", ["iphone"] = "айфон",
            ["ipad"] = "айпэд", ["app"] = "эпп", ["apple"] = "эпл",
            ["windows"] = "виндоуз", ["microsoft"] = "майкрософт",
            ["chrome"] = "кроум", ["firefox"] = "файрфокс", ["safari"] = "сафари",
            ["edge"] = "эдж", ["opera"] = "опера", ["android"] = "андроид",
            ["samsung"] = "самсунг", ["tesla"] = "тесла", ["spacex"] = "спейсэкс",
            ["twitter"] = "твиттер", ["facebook"] = "фейсбук",
            ["instagram"] = "инстаграм", ["tiktok"] = "тикток",
            ["whatsapp"] = "ватсап", ["telegram"] = "телеграм",
            ["netflix"] = "нетфликс", ["disney"] = "дисней", ["amazon"] = "амазон",
            ["wifi"] = "вай-фай", ["wi-fi"] = "вай-фай", ["bluetooth"] = "блютус",
            ["online"] = "онлайн", ["offline"] = "офлайн", ["email"] = "имейл",
            ["website"] = "вебсайт", ["web"] = "вэб", ["browser"] = "браузер",
            ["hacker"] = "хакер", ["password"] = "пассворд",
            ["username"] = "юзернэйм", ["login"] = "логин",
            ["account"] = "акаунт", ["download"] = "даунлоад",
            ["upload"] = "аплоад", ["streaming"] = "стриминг",
            ["stream"] = "стрим", ["server"] = "сэрвэр", ["startup"] = "стартап",
            ["thriller"] = "триллер", ["horror"] = "хоррор", ["action"] = "экшн",
            ["season"] = "сизон", ["episode"] = "эпизод", ["trailer"] = "трейлэр",
            ["release"] = "рилиз", ["update"] = "апдейт", ["upgrade"] = "апгрейд",
            ["version"] = "вэржн", ["phone"] = "фон", ["home"] = "хоум",
            ["money"] = "мани", ["time"] = "тайм", ["cool"] = "кул",
            ["okay"] = "окей", ["ok"] = "окей", ["wow"] = "вау",
            ["bye"] = "бай", ["hello"] = "хэллоу", ["hi"] = "хай",
            ["stop"] = "стоп", ["play"] = "плэй", ["pause"] = "пауза",
            ["next"] = "нэкст", ["cloud"] = "клауд", ["chip"] = "чип",
            ["team"] = "тим", ["game"] = "гэйм", ["project"] = "проджект"
        };

    private static readonly (string Suffix, string Sound)[] EnglishDigraphs =
    [
        ("eigh", "эй"), ("ough", "о"), ("tch", "ч"), ("sch", "ш"), ("dge", "дж"),
        ("sh", "ш"), ("ch", "ч"), ("th", "т"), ("ph", "ф"), ("wh", "в"),
        ("oh", "о"), ("ck", "к"), ("qu", "кв"), ("gh", "г"), ("oo", "у"), ("ee", "и"),
        ("ea", "и"), ("ai", "эй"), ("ay", "эй"), ("ei", "эй"), ("ey", "эй"),
        ("oi", "ой"), ("oy", "ой"), ("ou", "ау"), ("ow", "ау"), ("au", "о"),
        ("aw", "о"), ("ie", "и"), ("ll", "л"), ("mm", "м"), ("nn", "н"),
        ("pp", "п"), ("rr", "р"), ("ss", "с"), ("tt", "т"), ("dd", "д"),
        ("gg", "г"), ("cc", "к"), ("mb", "м")
    ];

    private static readonly Dictionary<char, string> EnglishLetterSounds = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е",
        ['f'] = "ф", ['g'] = "г", ['h'] = "х", ['i'] = "и", ['j'] = "дж",
        ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о",
        ['p'] = "п", ['q'] = "к", ['r'] = "р", ['s'] = "с", ['t'] = "т",
        ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс", ['y'] = "и",
        ['z'] = "з"
    };

    private static readonly Dictionary<char, string> EnglishLetterNames = new()
    {
        ['a'] = "эй", ['b'] = "би", ['c'] = "си", ['d'] = "ди", ['e'] = "и",
        ['f'] = "эф", ['g'] = "джи", ['h'] = "эйч", ['i'] = "ай", ['j'] = "джей",
        ['k'] = "кей", ['l'] = "эл", ['m'] = "эм", ['n'] = "эн", ['o'] = "оу",
        ['p'] = "пи", ['q'] = "кью", ['r'] = "ар", ['s'] = "эс", ['t'] = "ти",
        ['u'] = "ю", ['v'] = "ви", ['w'] = "дабл-ю", ['x'] = "экс",
        ['y'] = "уай", ['z'] = "зэд"
    };

    /// <summary>
    /// Переводит латинские слова в русскую фонетику до синтеза: Silero и SAPI
    /// читают английский побуквенно («чроме», «ипхоне»). Диграфы, мягкие
    /// c/g и аббревиатуры по названиям букв дают разборчивое произношение
    /// имён и терминов, не покинув оффлайн-словарь.
    /// </summary>
    internal static string TransliterateEnglish(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return EnglishWordPattern().Replace(text, match =>
        {
            var word = match.Value;
            if (EnglishPronunciations.TryGetValue(word, out var known)) return known;
            var letters = word.ToLowerInvariant();
            if (word.Length <= 5 && word.ToUpperInvariant() == word && letters.Length >= 2)
                return string.Join("-", letters.Select(IncludeLetterName));
            var builder = new StringBuilder(word.Length * 2);
            for (var index = 0; index < letters.Length;)
            {
                var matched = false;
                foreach (var (suffix, sound) in EnglishDigraphs)
                {
                    if (!letters.AsSpan(index).StartsWith(suffix,
                            StringComparison.Ordinal)) continue;
                    builder.Append(sound);
                    index += suffix.Length;
                    matched = true;
                    break;
                }
                if (matched) continue;
                var letter = letters[index];
                var next = index + 1 < letters.Length ? letters[index + 1] : '\0';
                if (letter == 'c' && next is 'e' or 'i' or 'y') builder.Append('с');
                else if (letter == 'g' && next is 'e' or 'i' or 'y') builder.Append("дж");
                else if (letter == 'y' && index == 0) builder.Append('й');
                else if (EnglishLetterSounds.TryGetValue(letter, out var sound))
                    builder.Append(sound);
                else builder.Append(letter);
                index++;
            }
            var result = builder.ToString();
            return result.Length == 0 ? word : result;
        });
    }

    private static string IncludeLetterName(char letter) =>
        EnglishLetterNames.TryGetValue(letter, out var name) ? name : letter.ToString();

    private static string ApplyPronunciationDictionary(string text)
    {
        var values = new Dictionary<string, string>(BuiltInPronunciations, StringComparer.OrdinalIgnoreCase);
        foreach (var item in LoadCustomPronunciations()) values[item.Key] = item.Value;
        foreach (var item in values.OrderByDescending(item => item.Key.Length))
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(item.Key)}(?![\p{{L}}\p{{N}}])";
            text = Regex.Replace(text, pattern, _ => item.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return text;
    }

    private static IReadOnlyDictionary<string, string> LoadCustomPronunciations()
    {
        var path = AppPaths.PronunciationDictionaryFile;
        if (string.IsNullOrWhiteSpace(AppPaths.AppRoot) || !File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumDictionaryBytes)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (DictionarySync)
            {
                if (path == _cachedDictionaryPath && info.LastWriteTimeUtc == _cachedDictionaryWriteUtc)
                    return _cachedCustomPronunciations;
                var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
                _cachedCustomPronunciations = stored
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value) &&
                                   item.Key.Length <= 100 && item.Value.Length <= 160)
                    .Take(256)
                    .ToDictionary(item => item.Key.Trim(), item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase);
                _cachedDictionaryPath = path;
                _cachedDictionaryWriteUtc = info.LastWriteTimeUtc;
                return _cachedCustomPronunciations;
            }
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ReplaceDate(Match match)
    {
        if (!int.TryParse(match.Groups[1].Value, out var day) ||
            !int.TryParse(match.Groups[2].Value, out var month) ||
            !int.TryParse(match.Groups[3].Value, out var year) ||
            day is < 1 or > 31 || month is < 1 or > 12 || year is < 1 or > 9999 ||
            day > DateTime.DaysInMonth(year, month))
            return match.Value;
        return $"{DayOrdinalGenitive[day]} {MonthGenitive[month]} {YearToWords(year)} года";
    }

    private static string ReplaceTime(Match match)
    {
        if (!int.TryParse(match.Groups[1].Value, out var hour) ||
            !int.TryParse(match.Groups[2].Value, out var minute) ||
            hour is < 0 or > 23 || minute is < 0 or > 59)
            return match.Value;
        var hours = $"{IntegerToWords(hour)} {CountForm(hour, "час", "часа", "часов")}";
        return minute == 0
            ? hours + " ровно"
            : $"{hours} {IntegerToWords(minute, feminine: true)} {CountForm(minute, "минута", "минуты", "минут")}";
    }

    private static string ReplaceTemperature(Match match)
    {
        var number = SpokenNumber(match.Groups[1].Value);
        return $"{number} {CountFormForWrittenNumber(match.Groups[1].Value, "градус Цельсия", "градуса Цельсия", "градусов Цельсия")}";
    }

    private static string ReplaceDataUnit(Match match)
    {
        var written = match.Groups[1].Value;
        var forms = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "КБ" or "KB" => ("килобайт", "килобайта", "килобайт"),
            "МБ" or "MB" => ("мегабайт", "мегабайта", "мегабайт"),
            "ГБ" or "GB" => ("гигабайт", "гигабайта", "гигабайт"),
            _ => ("терабайт", "терабайта", "терабайт")
        };
        return $"{SpokenNumber(written)} {CountFormForWrittenNumber(written, forms.Item1, forms.Item2, forms.Item3)}";
    }

    private static string ReplacePercent(Match match)
    {
        var written = match.Groups[1].Value;
        return $"{SpokenNumber(written)} {CountFormForWrittenNumber(written, "процент", "процента", "процентов")}";
    }

    private static string ReplaceRubles(Match match) => ReplaceCurrency(match, "рубль", "рубля", "рублей");
    private static string ReplaceDollars(Match match) => ReplaceCurrency(match, "доллар", "доллара", "долларов");
    private static string ReplaceEuros(Match match) => $"{SpokenNumber(CurrencyValue(match))} евро";

    private static string ReplaceCurrency(Match match, string one, string few, string many)
    {
        var written = CurrencyValue(match);
        return $"{SpokenNumber(written)} {CountFormForWrittenNumber(written, one, few, many)}";
    }

    private static string CurrencyValue(Match match) =>
        match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

    private static string SpokenNumber(string written)
    {
        var normalized = written.Replace('.', ',');
        var parts = normalized.Split(',', 2);
        return parts.Length == 2
            ? DecimalToWords(parts[0], parts[1])
            : IntegerToWords(ParseInteger(parts[0]));
    }

    private static string CountFormForWrittenNumber(string written, string one, string few, string many)
    {
        if (written.Contains(',') || written.Contains('.')) return few;
        return CountForm(ParseInteger(written), one, few, many);
    }

    private static string DecimalToWords(string wholeValue, string fractionalValue)
    {
        var whole = ParseInteger(wholeValue);
        var digits = fractionalValue.TrimEnd('0');
        if (digits.Length == 0) return IntegerToWords(whole);
        if (digits.Length > 3) digits = digits[..3];
        var fraction = ParseInteger(digits);
        var denominator = digits.Length switch
        {
            1 => CountForm(fraction, "десятая", "десятых", "десятых"),
            2 => CountForm(fraction, "сотая", "сотых", "сотых"),
            _ => CountForm(fraction, "тысячная", "тысячных", "тысячных")
        };
        return $"{IntegerToWords(whole, feminine: true)} {CountForm(whole, "целая", "целых", "целых")} " +
               $"{IntegerToWords(fraction, feminine: true)} {denominator}";
    }

    private static string YearToWords(int year)
    {
        if (year == 1900) return "тысяча девятисотого";
        if (year is > 1900 and < 2000)
            return "тысяча девятьсот " + OrdinalGenitive(year - 1900);
        if (year == 2000) return "двухтысячного";
        if (year is > 2000 and < 2100)
            return "две тысячи " + OrdinalGenitive(year - 2000);
        return IntegerToWords(year);
    }

    private static string OrdinalGenitive(int value)
    {
        string[] units = ["нулевого", "первого", "второго", "третьего", "четвёртого", "пятого", "шестого", "седьмого", "восьмого", "девятого"];
        string[] teens = ["десятого", "одиннадцатого", "двенадцатого", "тринадцатого", "четырнадцатого", "пятнадцатого", "шестнадцатого", "семнадцатого", "восемнадцатого", "девятнадцатого"];
        string[] tens = ["", "", "двадцатого", "тридцатого", "сорокового", "пятидесятого", "шестидесятого", "семидесятого", "восьмидесятого", "девяностого"];
        if (value < 10) return units[value];
        if (value < 20) return teens[value - 10];
        var rest = value % 10;
        return rest == 0 ? tens[value / 10] : Tens[value / 10] + " " + units[rest];
    }

    private static string IntegerToWords(long value, bool feminine = false)
    {
        if (value == 0) return "ноль";
        if (value == long.MinValue) return "минус девять квинтиллионов";
        var negative = value < 0;
        if (negative) value = -value;
        var groups = new List<string>();
        var scales = new (string One, string Few, string Many, bool Feminine)[]
        {
            ("", "", "", feminine),
            ("тысяча", "тысячи", "тысяч", true),
            ("миллион", "миллиона", "миллионов", false),
            ("миллиард", "миллиарда", "миллиардов", false),
            ("триллион", "триллиона", "триллионов", false),
            ("квадриллион", "квадриллиона", "квадриллионов", false),
            ("квинтиллион", "квинтиллиона", "квинтиллионов", false)
        };
        var scale = 0;
        while (value > 0 && scale < scales.Length)
        {
            var triad = (int)(value % 1000);
            if (triad != 0)
            {
                var words = TriadToWords(triad, scales[scale].Feminine);
                if (scale > 0) words += " " + CountForm(triad, scales[scale].One, scales[scale].Few, scales[scale].Many);
                groups.Insert(0, words);
            }
            value /= 1000;
            scale++;
        }
        return (negative ? "минус " : string.Empty) + string.Join(" ", groups);
    }

    private static string TriadToWords(int value, bool feminine)
    {
        var words = new List<string>(3);
        if (value / 100 > 0) words.Add(Hundreds[value / 100]);
        var remainder = value % 100;
        if (remainder is >= 10 and <= 19)
        {
            words.Add(Teens[remainder - 10]);
        }
        else
        {
            if (remainder / 10 > 0) words.Add(Tens[remainder / 10]);
            var ones = remainder % 10;
            if (ones > 0) words.Add((feminine ? FeminineOnes : MasculineOnes)[ones]);
        }
        return string.Join(" ", words);
    }

    private static string CountForm(long value, string one, string few, string many)
    {
        value = Math.Abs(value) % 100;
        if (value is >= 11 and <= 19) return many;
        return (value % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static long ParseInteger(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    [GeneratedRegex(@"[A-Za-z]+(?:['-][A-Za-z]+)*")]
    private static partial Regex EnglishWordPattern();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})[./-](\d{1,2})[./-](\d{4})(?!\d)")]
    private static partial Regex DatePattern();
    [GeneratedRegex(@"(?<!\d)([01]?\d|2[0-3]):([0-5]\d)(?!\d)")]
    private static partial Regex TimePattern();
    [GeneratedRegex(@"(?:№|No\.?)\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberSignPattern();
    [GeneratedRegex(@"(?<![\d.,])(-?\d+(?:[.,]\d+)?)\s*°\s*[CcСс](?!\p{L})")]
    private static partial Regex TemperaturePattern();
    [GeneratedRegex(@"(?<![\d.,])(-?\d+(?:[.,]\d+)?)\s*(КБ|МБ|ГБ|ТБ|KB|MB|GB|TB)(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex DataUnitPattern();
    [GeneratedRegex(@"(?<![\d.,])(-?\d+(?:[.,]\d+)?)\s*%(?!\p{L})")]
    private static partial Regex PercentPattern();
    [GeneratedRegex(@"(?<![\d.,])(-?\d+(?:[.,]\d+)?)\s*(?:₽|руб\.?|рублей)(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RublePattern();
    [GeneratedRegex(@"(?:\$\s*(-?\d+(?:[.,]\d+)?)|(-?\d+(?:[.,]\d+)?)\s*(?:\$|долл\.?))(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex DollarPattern();
    [GeneratedRegex(@"(?:€\s*(-?\d+(?:[.,]\d+)?)|(-?\d+(?:[.,]\d+)?)\s*(?:€|евро))(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex EuroPattern();
    [GeneratedRegex(@"(?<![\d.,])(-?\d+)[.,](\d+)(?![\d.,])")]
    private static partial Regex DecimalPattern();
    [GeneratedRegex(@"(?<![\p{L}\d.,])-?\d+(?![\p{L}\d.,])")]
    private static partial Regex IntegerPattern();
    [GeneratedRegex(@"([!?.,;:])\1+")]
    private static partial Regex RepeatedPunctuationPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
