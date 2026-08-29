using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NexusMonach.Services;
using NexusMonach.Services.Chat;
using System.Diagnostics;
using NexusMonach.Services.Planner;
using PlannerTaskStatus = NexusMonach.Services.Planner.TaskStatus;

namespace NexusMonach.Views;

/// <summary>
/// Вертикальная панель «Планировщик»: задачи (локально, экспорт .ics и
/// почта) и защищённый обмен (без сервера, исчезающая переписка, потоковая
/// выжимка в память). Все события озвучиваются.
/// </summary>
public partial class PlannerDockControl : UserControl
{
    private const int ChatPort = 9477;
    private ChatCrypto.Identity? _identity;
    private ChatSession? _session;
    private string _roomName = "комната";

    public sealed class TaskRow
    {
        public required PlannerTask Task { get; init; }
        public bool Done { get => Task.Status == PlannerTaskStatus.Done; set { } }
        public string Title => Task.Status == PlannerTaskStatus.Done ? "✔ " + Task.Title : Task.Title;
        public string Meta => (Task.DueUtc is { } due ? "срок " + due.ToLocalTime().ToString("dd.MM HH:mm") + " · " : "") +
            Task.Source;
        public Guid Id => Task.Id;
        public PlannerTask Payload => Task;
    }

    public sealed class ChatRow
    {
        public required string Line { get; init; }
        public required string Meta { get; init; }
        public required ChatMessage Message { get; init; }
    }

    public PlannerDockControl()
    {
        InitializeComponent();
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        TaskStore.Changed += RefreshTasks;
        RefreshTasks();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e) =>
        TaskStore.Changed -= RefreshTasks;

    // ── Задачи ────────────────────────────────────────────────────

    private void RefreshTasks()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshTasks);
            return;
        }
        var tasks = TaskStore.All;
        TasksList.ItemsSource = tasks
            .OrderBy(t => t.Status == PlannerTaskStatus.Done)
            .ThenBy(t => t.DueUtc ?? DateTimeOffset.MaxValue)
            .Select(t => new TaskRow { Task = t }).ToList();
        var open = tasks.Count(t => t.Status == PlannerTaskStatus.Open);
        TasksSummary.Text = $"открыто {open} · выполнено {tasks.Count - open} · всё локально";
    }

    private void AddTask_Click(object sender, RoutedEventArgs e) => AddFromBox();

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddFromBox();
    }

    private void AddFromBox()
    {
        var title = NewTaskBox.Text.Trim();
        if (title.Length == 0) return;
        TaskStore.Add(title);
        NewTaskBox.Clear();
        VoiceAssistantService.Announce("Задача добавлена: " + title,
            VoiceAnnouncementPriority.Progress);
    }

    private void TaskToggle_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.DataContext is not TaskRow row) return;
        var now = row.Task.Status != PlannerTaskStatus.Done;
        TaskStore.SetStatus(row.Id, now ? PlannerTaskStatus.Done : PlannerTaskStatus.Open);
        VoiceAssistantService.Announce(now ? "Задача выполнена: " + row.Task.Title : "Задача снова открыта",
            VoiceAnnouncementPriority.Progress);
    }

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskRow row)
            TaskStore.Remove(row.Id);
    }

    private void MailTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskRow row) return;
        try
        {
            Process.Start(new ProcessStartInfo(TaskStore.BuildMailto(row.Payload))
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("planner", "mailto", ex);
        }
    }

    private void ExportIcs_Click(object sender, RoutedEventArgs e)
    {
        var tasks = TaskStore.All;
        if (tasks.Count(t => t.DueUtc is not null) == 0)
        {
            VoiceAssistantService.Announce("Задач со сроками нет — экспортировать нечего.",
                VoiceAnnouncementPriority.Progress);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт задач в календарь",
            Filter = "Календарь iCalendar (*.ics)|*.ics",
            FileName = "nexus-задачи.ics"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dialog.FileName, TaskStore.BuildIcs(tasks));
        VoiceAssistantService.Announce("Календарь задач сохранён. Файл открывается в любом календаре.",
            VoiceAnnouncementPriority.Important);
    }

    // ── Вкладки ───────────────────────────────────────────────────

    private void TasksTab_Checked(object sender, RoutedEventArgs e)
    {
        if (TasksPane is null) return;
        TasksPane.Visibility = Visibility.Visible;
        ChatPane.Visibility = Visibility.Collapsed;
        StatusRun.Text = "· задачи";
    }

    private void ChatTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ChatPane is null) return;
        TasksPane.Visibility = Visibility.Collapsed;
        ChatPane.Visibility = Visibility.Visible;
        StatusRun.Text = "· обмен";
        if (_identity is null)
            _identity = new ChatCrypto.Identity();
    }

    // ── Защищённый обмен ──────────────────────────────────────────

    private ChatSession EnsureSession()
    {
        if (_session is not null) return _session;
        _identity ??= new ChatCrypto.Identity();
        _session = new ChatSession(_identity, DisplayNameBox.Text.Trim());
        _session.MessageReceived += OnMessage;
        _session.MemberChanged += (member, joined) => Dispatcher.Invoke(() =>
        {
            ChatState.Text = (joined ? "Вошёл: " : "Вышел: ") + member.Name +
                " (" + member.Fingerprint + ")";
            VoiceAssistantService.Announce(
                (joined ? "Участник вошёл: " : "Участник вышел: ") + member.Name,
                SpeakPriority());
        });
        _session.StateChanged += state => Dispatcher.Invoke(() =>
        {
            ChatState.Text = state;
            VoiceAssistantService.Announce(state, SpeakPriority());
        });
        return _session;
    }

    private static VoiceAnnouncementPriority SpeakPriority() =>
        VoiceAnnouncementPriority.Progress;

    private void OnMessage(ChatMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            AppendChatRow(message);
            // Потоковая выжимка: маркеры уходят в задачи и граф сразу.
            var (tasks, facts) = ChatGraphBridge.Absorb(message, _roomName);
            if (tasks + facts > 0)
                VoiceAssistantService.Announce(
                    $"Зафиксировано: задач {tasks}, знаний {facts}. Переписка исчезнет — это останется.",
                    VoiceAnnouncementPriority.Important);
            if (message.IsMedia && message.MediaPath is not null)
                VoiceAssistantService.Announce("Получен файл: " + message.MediaName,
                    VoiceAnnouncementPriority.Progress);
        });
    }

    private void AppendChatRow(ChatMessage message)
    {
        var items = ChatList.ItemsSource as List<ChatRow> ?? [];
        items.Add(new ChatRow
        {
            Line = message.IsMedia
                ? "📎 " + message.Author + ": " + message.MediaName
                : message.Author + ": " + message.Text,
            Meta = message.SentUtc.ToLocalTime().ToString("HH:mm:ss"),
            Message = message
        });
        ChatList.ItemsSource = items;
        ChatList.Items.Refresh();
        ChatList.ScrollIntoView(ChatList.Items[^1]);
    }

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = EnsureSession();
            _roomName = "комната-" + DateTime.Now.ToString("HHmm");
            await session.CreateRoomAsync(_roomName, ChatPort);
            ChatState.Text = $"Комната «{_roomName}» создана. Ваш ключ: " +
                ChatCrypto.Fingerprint(_identity!.PublicKey) +
                $". Приглашайте инвайтом — адрес собеседнику: ваш_адрес:{ChatPort}";
            VoiceAssistantService.Announce("Комната создана. Пригласите собеседника инвайтом.",
                VoiceAnnouncementPriority.Important);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("chat", "create-room", ex);
            ChatState.Text = "Не удалось создать комнату: " + ex.Message;
        }
    }

    private void MakeInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || !_session.IsAnchor)
        {
            ChatState.Text = "Инвайт создаёт якорь: сначала «Создать комнату».";
            return;
        }
        var keyDialog = new InputForPublicKeyWindow
        {
            Owner = Window.GetWindow(this)
        };
        if (keyDialog.ShowDialog() != true || keyDialog.PublicKeyBytes is not { Length: 64 } key)
        {
            ChatState.Text = "Для инвайта нужен публичный ключ собеседника (64 байта, base64).";
            return;
        }
        var host = "127.0.0.1"; // для локальной сети; для интернета — внешний адрес
        var invite = _session.BuildInvite(_roomName, key, host, ChatPort);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить инвайт",
            Filter = "Инвайт Nexus (*.nexusinvite)|*.nexusinvite",
            FileName = "invite-" + _roomName + ".nexusinvite"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dialog.FileName, invite.Serialize());
        VoiceAssistantService.Announce("Инвайт сохранён. Передайте файл собеседнику любым каналом.",
            VoiceAnnouncementPriority.Important);
    }

    private async void JoinByInvite_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть инвайт",
            Filter = "Инвайт Nexus (*.nexusinvite)|*.nexusinvite|Все файлы|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var invite = ChatInvite.TryParse(File.ReadAllText(dialog.FileName));
        if (invite is null)
        {
            ChatState.Text = "Инвайт повреждён или не распознан.";
            return;
        }
        // Проверка адресата: инвайт завёрнут на наш ключ.
        if (!invite.InviteePublicKey.AsSpan().SequenceEqual(_identity!.PublicKey))
        {
            ChatState.Text = "Инвайт выдан другому ключу — он не ваш.";
            return;
        }
        try
        {
            var session = EnsureSession();
            await session.JoinAsync(invite);
            VoiceAssistantService.Announce("Подключаемся к комнате " + invite.RoomName,
                VoiceAnnouncementPriority.Important);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("chat", "join", ex);
            ChatState.Text = "Не удалось подключиться: " + ex.Message;
        }
    }

    private async void SendMessage_Click(object sender, RoutedEventArgs e) => await SendFromBox();

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendFromBox();
    }

    private async Task SendFromBox()
    {
        var text = MessageBox.Text.Trim();
        if (text.Length == 0 || _session is null) return;
        MessageBox.Clear();
        try
        {
            await _session.SendTextAsync(text);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("chat", "send", ex);
            ChatState.Text = "Отправка не удалась: " + ex.Message;
        }
    }

    private async void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var dialog = new OpenFileDialog { Title = "Прикрепить файл" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await _session.SendMediaAsync(dialog.FileName, bytes);
            VoiceAssistantService.Announce("Файл отправлен: " + Path.GetFileName(dialog.FileName),
                VoiceAnnouncementPriority.Progress);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("chat", "attach", ex);
        }
    }

    // ── Сворачивание ──────────────────────────────────────────────

    private async void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        if (_session is { } session)
        {
            // Сворачивание не убивает сессию — она живёт, пока участник онлайн.
            await Task.CompletedTask;
        }
        VoiceAssistantService.Announce("Панель планировщика скрыта. Вернуть — кнопка План.",
            VoiceAnnouncementPriority.Progress);
    }
}
