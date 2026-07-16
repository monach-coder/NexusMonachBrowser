NEXUS MONACH OFFLINE AI PACK

Эта папка копируется рядом с NexusMonach.exe как AI\.
Во время работы браузер никогда не загружает модели или исполняемые файлы.

Полная release-сборка содержит:

AI\llama\llama-cli.exe, AI\llama\llama-server.exe и DLL из официального Windows x64-релиза llama.cpp
AI\whisper\whisper-cli.exe и DLL из официального Windows x64-релиза whisper.cpp
AI\models\qwen3-0.6b\Qwen3-0.6B-Q8_0.gguf
AI\models\whisper\ggml-base-q5_1.bin
AI\models\multilingual-e5-small\*.onnx + tokenizer
AI\models\smolvlm-500m\SmolVLM-500M-Instruct-Q8_0.gguf + mmproj
AI\models\translation\mul-en\*.onnx + tokenizer (многоязычный текст → английский)
AI\models\translation\ko-en\*.onnx + tokenizer (корейский текст → английский)
AI\models\translation\en-ru\*.onnx + tokenizer (английский → русский)

Перевод страницы и выделения выполняют специализированные OPUS-MT модели.
Qwen в этот путь не входит. Для видео Whisper распознаёт и переводит речь в
английский, после чего OPUS-MT en-ru создаёт русскую строку субтитров. Node/ONNX
процесс остаётся запущенным между репликами и не загружает веса заново.

Файлы моделей не входят в маленький source-архив из-за их размера. Они должны входить
в публикуемый Full Offline архив. Build-Portable.ps1 явно показывает состояние комплекта.
Не подменяйте файлы моделями из неизвестных репозиториев.
