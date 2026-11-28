using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using PersonalFlow.Client;

namespace PersonalFlow.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
            life.MainWindow = new FlowWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed partial class FlowWindow : Window
{
    private readonly string appDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalFlow");
    private readonly LocalStore store;
    private readonly DesktopIdentitySession identity;
    private readonly DesktopSyncCoordinator synchronization;
    private readonly StackPanel body = new() { Spacing = 18, Margin = new Thickness(32, 28) };
    private DateOnly anchor = LocalStore.Today;
    private bool monthView;

    private static readonly IBrush Accent = Brush.Parse("#315943");
    private static readonly IBrush Muted = Brush.Parse("#69776d");
    private static readonly IBrush Canvas = Brush.Parse("#f4f6f1");
    private static readonly IBrush Border = Brush.Parse("#e5e9e2");

    public FlowWindow()
    {
        Directory.CreateDirectory(appDataDirectory);
        store = new(Path.Combine(appDataDirectory, "personalflow.db"));
        identity = new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new DesktopCredentialStore(), appDataDirectory);
        synchronization = new(store, identity);
        PrivateSeedImporter.ImportIfPresent(store);
        Title = "PersonalFlow";
        Width = 1280;
        Height = 880;
        MinWidth = 900;
        MinHeight = 650;
        Background = Canvas;

        var root = new DockPanel();
        var sidebar = new StackPanel { Width = 218, Spacing = 8, Margin = new Thickness(18, 22) };
        DockPanel.SetDock(sidebar, Dock.Left);
        sidebar.Children.Add(new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(12, 4, 8, 28),
            Children =
            {
                new TextBlock { Text = "personalflow", FontSize = 25, FontWeight = FontWeight.Bold, Foreground = Accent },
                new TextBlock { Text = "Личное пространство", FontSize = 12, Foreground = Muted }
            }
        });
        sidebar.Children.Add(NavButton("Планы", ShowPlans));
        sidebar.Children.Add(NavButton("Обучение", () => ShowLearning()));
        sidebar.Children.Add(NavButton("Заметки", ShowNotes));
        sidebar.Children.Add(NavButton("Тренировки", ShowTraining));
        sidebar.Children.Add(new Border { Height = 1, Background = Border, Margin = new Thickness(10, 16) });
        sidebar.Children.Add(NavButton("Настройки", ShowSettings));
        sidebar.Children.Add(new Border
        {
            Margin = new Thickness(10, 18, 10, 0),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.White,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Офлайн", FontWeight = FontWeight.SemiBold, Foreground = Accent },
                    new TextBlock { Text = "Данные хранятся на этом устройстве", FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap }
                }
            }
        });
        root.Children.Add(sidebar);

        var contentArea = new DockPanel();
        var topBar = new Border
        {
            Background = Brushes.White,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(28, 14),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = "Локальные данные · Европа/Москва", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Личная панель", Foreground = Accent, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        DockPanel.SetDock(topBar, Dock.Top);
        contentArea.Children.Add(topBar);
        contentArea.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        root.Children.Add(contentArea);
        Content = root;
        ShowPlans();
    }

    private static Button NavButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 11),
            MinHeight = 44,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Accent,
            FontWeight = FontWeight.Medium
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(15, 10),
            MinHeight = 42,
            Background = primary ? Accent : Brushes.White,
            Foreground = primary ? Brushes.White : Accent,
            BorderBrush = primary ? Accent : Border,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBox Input(string hint, string? value = null, bool multiline = false) => new()
    {
        PlaceholderText = hint,
        Text = value ?? string.Empty,
        MinHeight = multiline ? 92 : 42,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        Padding = new Thickness(11, 9)
    };

    private static TextBlock Text(string value, double size = 15, bool muted = false) => new()
    {
        Text = value,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = muted ? Muted : Accent
    };

    private static Border Card(Control content) => new()
    {
        Background = Brushes.White,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(18),
        Child = content
    };

    private static StackPanel Row(double spacing = 10) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = spacing,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static WrapPanel Wrap() => new() { Orientation = Orientation.Horizontal };

    private static DatePicker DatePicker(DateOnly date) => new()
    {
        SelectedDate = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        MinHeight = 42
    };

    private static DateOnly PickedDate(DatePicker picker, DateOnly fallback) => picker.SelectedDate is { } value
        ? DateOnly.FromDateTime(value.DateTime)
        : fallback;

    private static void Heading(StackPanel host, string title, string subtitle)
    {
        host.Children.Clear();
        host.Children.Add(Text(title, 30));
        host.Children.Add(Text(subtitle, 14, muted: true));
    }

    private Window Dialog(string title, double width, double height, Control content) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 520),
        MinHeight = Math.Min(height, 400),
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Canvas,
        Content = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 14, Margin = new Thickness(24), Children = { content } },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        }
    };

    private async Task ShowModalAsync(Window dialog) => await dialog.ShowDialog(this);

    private void ConfirmDestructive(string title, string message, string confirmLabel, Action confirm)
    {
        var content = new StackPanel { Spacing = 12, Children = { Text(title, 21), Text(message, 14, muted: true) } };
        var dialog = Dialog(title, 540, 300, content);
        var actions = Wrap();
        actions.Children.Add(Button(confirmLabel, () => { dialog.Close(); confirm(); }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        content.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void ShowPlans() => RenderPlanner();

    private void ShowNotes()
    {
        Heading(body, "Заметки", "Поиск, закрепление и редактирование личных текстов.");
        var search = Input("Поиск по заметкам");
        body.Children.Add(search);
        var compose = Input("Новая заметка или сообщение", multiline: true);
        body.Children.Add(compose);
        body.Children.Add(Button("Сохранить заметку", () =>
        {
            if (string.IsNullOrWhiteSpace(compose.Text)) return;
            var note = new NoteItem(Guid.NewGuid(), compose.Text.Trim(), DateTimeOffset.UtcNow);
            store.Save(note.Id, note);
            ShowNotes();
        }, primary: true));
        var list = new StackPanel { Spacing = 12 };
        body.Children.Add(list);

        void Render()
        {
            list.Children.Clear();
            var query = search.Text?.Trim() ?? string.Empty;
            foreach (var note in store.All<NoteItem>()
                         .Where(item => item.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(item => item.Pinned).ThenByDescending(item => item.Created))
            {
                var editor = Input("Текст", note.Text, multiline: true);
                var actions = Row();
                actions.Children.Add(Button("Сохранить", () =>
                {
                    store.Save(note.Id, note with { Text = editor.Text?.Trim() ?? string.Empty });
                    Render();
                }));
                actions.Children.Add(Button(note.Pinned ? "Открепить" : "Закрепить", () =>
                {
                    store.Save(note.Id, note with { Pinned = !note.Pinned });
                    Render();
                }));
                actions.Children.Add(Button("Удалить", () => ConfirmDestructive("Удалить заметку?",
                    "Текст будет удалён с этого устройства.", "Удалить заметку", () =>
                    {
                        store.Delete<NoteItem>(note.Id);
                        Render();
                    })));
                list.Children.Add(Card(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        Text($"{(note.Pinned ? "Закреплено · " : string.Empty)}{note.Created.ToLocalTime():dd MMM yyyy, HH:mm}", 12, muted: true),
                        editor,
                        actions
                    }
                }));
            }
        }

        search.TextChanged += (_, _) => Render();
        Render();
    }

    private void ShowLearning(Guid? selectedLessonId = null, int? selectedVersion = null)
    {
        var cachedLessons = store.GetCachedLessons();
        var selected = selectedLessonId is { } requestedId && selectedVersion is { } requestedVersion
            ? store.GetCachedLessonContent(requestedId, requestedVersion)
            : cachedLessons
                .OrderBy(x => x.Date < LocalStore.Today ? 1 : 0)
                .ThenBy(x => Math.Abs(x.Date.DayNumber - LocalStore.Today.DayNumber))
                .Select(x => store.GetCachedLessonContent(x.Id, x.Version))
                .FirstOrDefault(x => x is not null);
        Heading(body, "Обучение", "Опубликованные уроки и прогресс доступны офлайн.");
        if (cachedLessons.Count > 0)
        {
            body.Children.Add(Text("Уроки на устройстве", 18));
            foreach (var cached in cachedLessons)
            {
                var lesson = store.GetCachedLessonContent(cached.Id, cached.Version);
                if (lesson is null) continue;
                body.Children.Add(Button($"{(selected is { } current && current.Id == lesson.Id && current.Version == lesson.Version ? "✓ " : "")}{lesson.Date:dd.MM.yyyy} · {lesson.Title} · v{lesson.Version}",
                    () => ShowLearning(lesson.Id, lesson.Version)));
            }
        }
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text(selected?.Title ?? "Демонстрационный урок", 22),
                Text(selected is null
                    ? "CancellationToken сообщает об отмене, но не завершает поток принудительно. Передавайте токен через асинхронные границы вплоть до операции ввода-вывода и корректно освобождайте ресурсы при отмене."
                    : $"{selected.Date:dd.MM.yyyy} · {selected.EstimatedMinutes} минут · версия {selected.Version}"),
                Text(selected?.Theory ?? "Уроки появятся здесь после загрузки опубликованного пакета.")
            }
        }));
        if (!string.IsNullOrWhiteSpace(selected?.SelfCheck))
            body.Children.Add(Card(new StackPanel { Spacing = 8, Children = { Text("Ориентиры для самопроверки", 18), Text(selected.SelfCheck) } }));
        var sources = selected?.Sources ?? [new LearningSource("Cancellation in managed threads", "https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads", null, new DateOnly(2026, 11, 27))];
        foreach (var source in sources)
        {
            body.Children.Add(Button($"Первоисточник · {source.Title}", () => OpenLearningSource(source.Url)));
            if (!string.IsNullOrWhiteSpace(source.Section)) body.Children.Add(Text(source.Section, 13, muted: true));
        }
        var id = selected?.Id ?? Guid.Parse("c1bff05b-f5ba-447e-ae92-6dd5b1824861");
        var version = selected?.Version ?? 1;
        var progress = store.GetLearningProgress(id, version);
        var practice = selected?.Practice;
        if (selected is null && LocalStore.Today.DayOfWeek != DayOfWeek.Sunday)
            practice = "Пользователь закрыл экран во время загрузки списка. Где вы передадите токен отмены и как отличите ожидаемую отмену от сетевой ошибки?";
        TextBox? answer = null;
        TextBox? evidence = null;
        if (practice is { Length: > 0 })
        {
            body.Children.Add(Card(new StackPanel { Spacing = 8, Children = { Text("Проверьте понимание", 18), Text(practice) } }));
            answer = Input("Ваш ответ", progress.Answer, multiline: true);
            evidence = Input("Что вы теперь можете объяснить или применить?", progress.Evidence, multiline: true);
            body.Children.Add(answer);
            body.Children.Add(evidence);
            if (selected is null)
                body.Children.Add(Text("Ориентиры: передать токен до I/O, не маскировать отмену под сетевую ошибку и освободить ресурсы.", 13, muted: true));
        }
        else if (selected?.Date.DayOfWeek == DayOfWeek.Sunday || selected is null && LocalStore.Today.DayOfWeek == DayOfWeek.Sunday)
        {
            body.Children.Add(Text("Воскресный урок — только теория, без обязательной практики.", 13, muted: true));
        }

        var notes = Input("Заметки к уроку", progress.Notes, multiline: true);
        var read = new CheckBox { Content = "Я прочитал урок", IsChecked = progress.Status == "Read", MinHeight = 40 };
        body.Children.Add(notes);
        body.Children.Add(read);
        body.Children.Add(Button("Сохранить прогресс", () =>
        {
            var isRead = read.IsChecked == true;
            progress = progress with
            {
                Answer = answer?.Text ?? progress.Answer,
                Notes = notes.Text ?? string.Empty,
                Evidence = evidence?.Text ?? progress.Evidence,
                Status = isRead ? "Read" : "InProgress",
                ViewedAt = isRead ? progress.ViewedAt ?? DateTimeOffset.UtcNow : progress.ViewedAt
            };
            store.SaveLearningProgress(progress);
        }, primary: true));
        var copy = Button("Скопировать пакет для Codex", async () =>
        {
            if (Clipboard is not null)
            {
                var updated = progress with
                {
                    Answer = answer?.Text ?? progress.Answer,
                    Notes = notes.Text ?? string.Empty,
                    Evidence = evidence?.Text ?? progress.Evidence
                };
                if (updated != progress)
                {
                    progress = updated;
                    store.SaveLearningProgress(progress);
                }
                await Clipboard.SetTextAsync($"Урок: {selected?.Title ?? "Отмена — это договорённость"}\n" +
                    $"Источники: {string.Join("; ", sources.Select(x => x.Url))}\n" +
                    $"Мой ответ: {progress.Answer}\nЗаметки: {progress.Notes}\nСвидетельство понимания: {progress.Evidence}\n" +
                    "Проверь понимание, отметь сильные стороны и пробелы, затем задай один уточняющий вопрос.");
            }
        });
        body.Children.Add(copy);
    }

    private static void OpenLearningSource(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowSettings()
    {
        Heading(body, "Настройки", "Состояние локального хранилища и подключений.");
        var syncStatus = store.SyncStatus;
        var identityAddress = Input("https://identity.example.net/", identity.IdentityAuthority);
        var lifeAddress = Input("https://app.example.net/life/", identity.LifeApi);
        var learningAddress = Input("https://app.example.net/learning/", identity.LearningApi);
        var sessionStatus = Text(ReadSessionStatus(), 14, muted: true);
        var operationStatus = Text("Синхронизация запускается вручную. При временной ошибке локальная очередь останется на устройстве.", 13, muted: true);
        var pendingStatus = Text($"В очереди локальных изменений: {syncStatus.Pending}", 14);
        var conflictStatus = Text($"Конфликты, требующие решения: {syncStatus.Conflicts}", 14);
        var epochStatus = Text($"Обнаруженные смены эпохи сервера: {syncStatus.EpochConflicts}", 14);

        async Task RunAsync(Func<Task> operation)
        {
            operationStatus.Text = "Выполняется…";
            try
            {
                await operation();
                var current = store.SyncStatus;
                pendingStatus.Text = $"В очереди локальных изменений: {current.Pending}";
                conflictStatus.Text = $"Конфликты, требующие решения: {current.Conflicts}";
                epochStatus.Text = $"Обнаруженные смены эпохи сервера: {current.EpochConflicts}";
                sessionStatus.Text = ReadSessionStatus();
            }
            catch (Exception error)
            {
                operationStatus.Text = error.Message;
            }
        }

        void SaveEndpointFields() => identity.SaveEndpoints(
            identityAddress.Text ?? string.Empty,
            lifeAddress.Text ?? string.Empty,
            learningAddress.Text ?? string.Empty);

        var actions = Row();
        actions.Children.Add(Button("Сохранить адреса", () =>
        {
            try
            {
                SaveEndpointFields();
                operationStatus.Text = "HTTPS-адреса сохранены на этом устройстве.";
            }
            catch (Exception error) { operationStatus.Text = error.Message; }
        }));
        actions.Children.Add(Button("Войти через браузер", () => _ = RunAsync(async () =>
        {
            SaveEndpointFields();
            await identity.SignInAsync();
            operationStatus.Text = "Вход выполнен. Токен хранится в системном хранилище учётных данных.";
        }), primary: true));
        actions.Children.Add(Button("Синхронизировать сейчас", () => _ = RunAsync(async () =>
        {
            SaveEndpointFields();
            var result = await synchronization.SynchronizeAsync();
            operationStatus.Text = $"Синхронизация завершена. Загружено новых уроков: {result.LessonsAdded}.";
        })));

        var accountActions = Row();
        accountActions.Children.Add(Button("Выйти из аккаунта", () => _ = RunAsync(async () =>
        {
            var revoked = await identity.SignOutAsync();
            sessionStatus.Text = ReadSessionStatus();
            operationStatus.Text = revoked
                ? "Вы вышли. Локальные планы, заметки, уроки и очередь изменений сохранены."
                : "Токен удалён с этого устройства; сервер не подтвердил отзыв. Локальные данные сохранены.";
        })));

        body.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Хранилище на устройстве", 20),
                Text(Path.Combine(appDataDirectory, "personalflow.db"), 13, muted: true),
                pendingStatus,
                conflictStatus,
                epochStatus,
                Text("Конфликты сохраняются локально и на сервере; интерфейс их разрешения для desktop ещё не готов.", 13, muted: true),
                Text("Обновление приложения не удаляет эту базу. Не удаляйте файл базы при устранении неполадок.", 13, muted: true)
            }
        }));

        body.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("Подключение к PersonalFlow", 20),
                Text("Укажите HTTPS-адреса API. Адреса хранятся в настройках приложения; токен обновления хранится отдельно в Windows Credential Manager или macOS Keychain.", 13, muted: true),
                Text("Identity API", 13), identityAddress,
                Text("Life API", 13), lifeAddress,
                Text("Learning API", 13), learningAddress,
                actions,
                sessionStatus,
                operationStatus,
                accountActions
            }
        }));
    }

    private string ReadSessionStatus()
    {
        try
        {
            return identity.HasSessionAsync().GetAwaiter().GetResult()
                ? "Сохранённый аккаунт готов к синхронизации."
                : "Аккаунт не подключён. Вход откроется в системном браузере.";
        }
        catch (Exception error) { return "Не удалось прочитать системное хранилище учётных данных: " + error.Message; }
    }
}
