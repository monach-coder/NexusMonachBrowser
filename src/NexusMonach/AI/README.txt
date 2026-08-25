NEXUS MONACH OFFLINE AI PACK

Эта папка копируется рядом с NexusMonach.exe как AI\.
Во время работы браузер никогда не загружает модели или исполняемые файлы.

Полная release-сборка содержит:

AI\llama\llama-cli.exe, AI\llama\llama-server.exe и DLL из официального Windows x64-релиза llama.cpp
AI\whisper\whisper-server.exe, whisper-cli.exe и DLL из официального Windows x64-релиза whisper.cpp
AI\models\qwen3-0.6b\Qwen3-0.6B-Q8_0.gguf
AI\models\whisper\ggml-base-q5_1.bin
AI\models\multilingual-e5-small\*.onnx + tokenizer
AI\models\smolvlm-500m\SmolVLM-500M-Instruct-Q8_0.gguf + mmproj
AI\models\translation\mul-en\*.onnx + tokenizer (многоязычный текст → английский)
AI\models\translation\ko-en\*.onnx + tokenizer (корейский текст → английский)
AI\models\translation\en-ru\*.onnx + tokenizer (английский → русский)
AI\voice\nexus-voice-worker.exe
AI\models\voice\vosk-tts-ru-multi\config.json, model.onnx и dictionary

Опциональный Piper HD pack, если лицензия конкретного голоса проверена:
AI\voice\nexus-piper-worker.exe
AI\models\voice\piper-hd\voice.onnx и voice.onnx.json

Код worker и поддержка профилей входят в исходники. Full Offline workflow
собирает worker, проверяет закреплённый архив модели и выполняет реальный
smoke-test синтеза до упаковки. Непроверенные бинарники и веса не загружаются
во время работы браузера. Если подписанный голосовой комплект отсутствует,
Nexus честно переключается на
установленный женский голос Windows.

Перед синтезом единый русский speech frontend раскрывает даты, время, числа,
единицы измерения, валюты и технические сокращения. Пользовательские исправления
произношений читаются из локального pronunciation-dictionary.json.

Основная статья переводится OPUS-MT в памяти и озвучивается женским голосом.
В DOM переводятся только меню, кнопки, подписи и подсказки форм. Перевод
выделения также выполняют специализированные OPUS-MT модели.
Qwen в этот путь не входит. Для видео постоянный whisper-server распознаёт
исходную речь без перевода, после чего OPUS-MT создаёт русскую реплику для
синхронной женской закадровой озвучки Nexus Neural Voice. Windows SAPI остаётся
резервом. Субтитры в DOM не создаются.
Оба AI-процесса остаются запущенными между репликами и не загружают веса заново.

Файлы моделей не входят в маленький source-архив из-за их размера. Они должны входить
в публикуемый Full Offline архив. Build-Portable.ps1 явно показывает состояние комплекта.
Не подменяйте файлы моделями из неизвестных репозиториев.
