namespace NexusMonach.Services;

/// <summary>
/// Пасскеи (WebAuthn/Windows Hello) в WebView2 не работают — известное
/// ограничение платформы (Microsoft Edge WebView2Feedback #5663). Вместо
/// молчаливого «Passkey registration failed» перехватываем отказ
/// credential-API на самой странице и предлагаем открыть её во внешнем
/// браузере, где пасскеи поддержаны нативно.
/// </summary>
internal static class WebAuthnFallback
{
    public const string MessageKind = "kuznec-webauthn-failed";

    /// <summary>
    /// Инъекция на каждую страницу: оборачивает credential-API и сообщает
    /// хосту об отказе. Хост сам решает, что показать пользователю —
    /// скрипт ничего не открывает и никуда не ходит.
    /// </summary>
    public const string Script = """
        (function () {
            'use strict';
            if (!navigator.credentials) return;
            var reported = false;
            function report(api, err) {
                if (reported) return;
                reported = true;
                try {
                    window.chrome.webview.postMessage({
                        kind: 'kuznec-webauthn-failed',
                        api: api,
                        name: err && err.name ? String(err.name) : ''
                    });
                } catch (_) { }
            }
            var create = navigator.credentials.create.bind(navigator.credentials);
            navigator.credentials.create = function (options) {
                return create(options).catch(function (e) { report('create', e); throw e; });
            };
            if (navigator.credentials.get) {
                var get = navigator.credentials.get.bind(navigator.credentials);
                navigator.credentials.get = function (options) {
                    return get(options).catch(function (e) { report('get', e); throw e; });
                };
            }
        })();
        """;
}
