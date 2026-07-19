Nexus Monach 2.7.2 — Translation Engine 3.0

Что исправлено:
- непрерывный захват системного звука вместо остановки каждые 7 секунд;
- постоянный whisper-server: модель загружается один раз;
- Whisper больше не переводит речь в английский режимом --translate;
- исходная речь отдельно переводится OPUS на русский;
- ограниченная очередь из двух аудиосегментов не раздувает память;
- удаление повторов на перекрытии соседних фрагментов;
- CI и установщик Full Offline требуют whisper-server.exe;
- журнал, README и описание архитектуры обновлены.

Установка патча в PowerShell:

Set-Location "D:\NexusMonach-Dev"
Expand-Archive `
  -Path "$env:USERPROFILE\Downloads\NexusMonach-2.7.2-translation-engine-3.0-patch.zip" `
  -DestinationPath "D:\NexusMonach-Dev\NexusMonachBrowser" `
  -Force
Set-Location "D:\NexusMonach-Dev\NexusMonachBrowser"
dotnet build NexusMonach.sln --configuration Release -warnaserror

После успешной локальной сборки проверьте git diff --check, создайте отдельную
ветку и Pull Request. Для реального теста видеоперевода соберите новый Full
Offline release: маленькая source-сборка не содержит whisper-server и моделей.
