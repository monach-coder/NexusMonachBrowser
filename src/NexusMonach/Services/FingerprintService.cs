namespace NexusMonach.Services;

/// <summary>
/// Стандартизация отпечатка браузера в Режиме Следа: не случайная подмена
/// (случайность = уникальность), а выравнивание под самый массовый Chrome.
/// UA строится из установленного Evergreen-рантайма без Edge-пометок,
/// canvas/WebGL получают лёгкий стабильный-на-сессию шум.
/// </summary>
public static class FingerprintService
{
    /// <summary>
    /// Строит стандартный UA Chrome из версии WebView2-рантайма.
    /// Чистая функция — проверяется юнит-тестами.
    /// </summary>
    public static string NormalizeUserAgent(string runtimeVersion)
    {
        // Версия рантайма может нести суффикс канала — берём четыре
        // числовые группы, остальное это не UA.
        var digits = new string(runtimeVersion.TakeWhile(char.IsDigit).ToArray());
        var groups = runtimeVersion.Split('.');
        var version = string.Join(".",
            groups.Take(4).Select(g => new string(g.TakeWhile(char.IsDigit).ToArray())));
        if (string.IsNullOrWhiteSpace(version) || !version.Contains('.'))
            version = digits.Length > 0 ? digits : "120.0.0.0";
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
               $"(KHTML, like Gecko) Chrome/{version} Safari/537.36";
    }

    /// <summary>
    /// Farbling-скрипт: лёгкий шум в canvas и обобщение WebGL-параметров.
    /// Внедряется до создания документа (AddScriptToExecuteOnDocumentCreation)
    /// — работает во всех фреймах до скриптов страницы.
    /// </summary>
    public const string FarbleScript = """
        (() => {
          // Сессионный seed: шум стабильный внутри сессии, разный между ними.
          let seed = 0;
          try { seed = crypto.getRandomValues(new Uint32Array(1))[0] || 1; } catch (_) { seed = 1; }
          const noise = (value, spread) => {
            const jitter = ((seed % 97) / 97 - 0.5) * spread;
            return value + jitter * value;
          };
          const wrap = (obj, name, farble) => {
            const original = obj[name];
            if (typeof original !== 'function') return;
            obj[name] = function (...args) {
              const result = original.apply(this, args);
              return farble(result);
            };
          };
          try {
            wrap(HTMLCanvasElement.prototype, 'toDataURL', data => {
              if (typeof data !== 'string' || !data.startsWith('data:image')) return data;
              // Точечная порча последнего пиксельного байта PNG-чанка: длина
              // та же, картинка визуально та же, хеш отпечатка меняется.
              return data.slice(0, -4) +
                String.fromCharCode(65 + (seed % 24)) +
                data.slice(-3);
            });
          } catch (_) {}
          try {
            const params = WebGLRenderingContext.prototype.getParameter;
            WebGLRenderingContext.prototype.getParameter = function (parameter) {
              // UNMASKED_VENDOR_WEBGL = 0x9245, UNMASKED_RENDERER_WEBGL = 0x9246
              if (parameter === 0x9245) return 'Google Inc. (Intel)';
              if (parameter === 0x9246)
                return 'ANGLE (Intel, Intel(R) UHD Graphics 630 (0x00003E9B) Direct3D11 vs_5_0 ps_5_0, D3D11)';
              return params.apply(this, [parameter]);
            };
          } catch (_) {}
          try {
            wrap(CanvasRenderingContext2D.prototype, 'measureText', metric => {
              if (!metric || !('width' in metric)) return metric;
              const cloned = new DOMMetricShim(metric.width + (seed % 7) * 0.01);
              return cloned;
            });
          } catch (_) {}
          function DOMMetricShim(width) { this.width = width; }
        })();
        """;
}
