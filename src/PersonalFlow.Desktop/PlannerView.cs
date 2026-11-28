using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using PersonalFlow.Client;
using PersonalFlow.Core;

namespace PersonalFlow.Desktop;

public sealed partial class FlowWindow
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private void RenderPlanner()
    {
        Heading(body, "Планы", "Неделя и месяц · повторы и периоды отсутствия · Московское время");
        var shortcuts = Wrap();
        shortcuts.Children.Add(Button($"Повторы  {store.All<Recurrence>().Count}", ShowRecurrenceManager));
        shortcuts.Children.Add(Button($"Отпуска и исключения  {store.All<Vacation>().Count}", ShowVacationManager));
        body.Children.Add(shortcuts);

        var toolbar = Wrap();
        toolbar.Children.Add(Button("‹", () => ShiftPeriod(-1)));
        toolbar.Children.Add(Text(anchor.ToString("MMMM yyyy", Russian), 20));
        toolbar.Children.Add(Button("›", () => ShiftPeriod(1)));
        toolbar.Children.Add(Button(monthView ? "Показать неделю" : "Показать месяц", () =>
        {
            monthView = !monthView;
            RenderPlanner();
        }));
        toolbar.Children.Add(Button("Сегодня", () => { anchor = LocalStore.Today; RenderPlanner(); }));
        toolbar.Children.Add(Button("+ Дело", () => EditTask(new PlanItem(Guid.NewGuid(), string.Empty, anchor)) , primary: true));
        body.Children.Add(toolbar);

        var first = monthView
            ? new DateOnly(anchor.Year, anchor.Month, 1)
            : anchor.AddDays(-((int)anchor.DayOfWeek + 6) % 7);
        if (monthView) first = first.AddDays(-((int)first.DayOfWeek + 6) % 7);
        var dayCount = monthView ? 42 : 7;
        var through = first.AddDays(dayCount - 1);
        var planned = RecurrenceProjection.Expand(first, through, store.All<Recurrence>(), store.All<Vacation>(), store.All<OccurrenceOverride>());
        var manual = store.All<PlanItem>();
        var finishedWorkouts = store.All<TrainingLog>().Where(log => log.Finished).Select(log => log.Id).ToHashSet();
        var vacations = store.All<Vacation>();
        var calendar = new WrapPanel { Orientation = Orientation.Horizontal };

        for (var index = 0; index < dayCount; index++)
        {
            var date = first.AddDays(index);
            var dayTasks = planned.Where(item => item.Date == date)
                .Select(item => (item.Title, item.Time, item.Done, item.WorkoutDayId, Recurring: (PlannedEntry?)item, Manual: (PlanItem?)null))
                .Concat(manual.Where(item => item.Date == date)
                    .Select(item => (item.Title, item.Time, item.Done, item.WorkoutDayId, Recurring: (PlannedEntry?)null, Manual: (PlanItem?)item)))
                .OrderBy(item => item.Time ?? "99:99", StringComparer.Ordinal)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .ToArray();
            var dayLeave = vacations.Where(item => date >= item.Start && date <= item.End).ToArray();
            var panel = new StackPanel { Spacing = 7 };
            panel.Children.Add(Text(date.ToString("ddd, dd MMM", Russian), 14));
            if (date == LocalStore.Today) panel.Children.Add(Text("Сегодня", 11));
            if (monthView && date.Month != anchor.Month) panel.Children.Add(Text("Другой месяц", 10, muted: true));
            foreach (var leave in dayLeave)
                panel.Children.Add(Text($"Отсутствие · {leave.Title}", 10, muted: true));

            if (dayTasks.Length == 0)
                panel.Children.Add(Text("Свободно", 11, muted: true));
            foreach (var task in dayTasks)
            {
                var scheduledSessionId = task.Manual?.Id ?? task.Recurring?.Id;
                var workoutFinished = task.WorkoutDayId is not null && scheduledSessionId is { } sessionId && finishedWorkouts.Contains(sessionId);
                var check = new CheckBox
                {
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            Text(task.Title, 12),
                            Text($"{(string.IsNullOrWhiteSpace(task.Time) ? "Без времени" : task.Time)}{(task.Recurring is null ? string.Empty : " · повтор")}{(task.WorkoutDayId is null ? string.Empty : " · тренировка")}", 10, muted: true)
                        }
                    },
                    IsChecked = task.Done || workoutFinished,
                    IsEnabled = !workoutFinished,
                    VerticalAlignment = VerticalAlignment.Top
                };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (task.Recurring is { } occurrence) SaveOccurrence(occurrence, done: check.IsChecked == true);
                    else if (task.Manual is { } item) store.Save(item.Id, item with { Done = check.IsChecked == true });
                    RenderPlanner();
                };
                panel.Children.Add(check);
                var actions = Wrap();
                actions.Children.Add(Button("Изменить", () =>
                {
                    if (task.Recurring is { } occurrence) EditOccurrence(occurrence);
                    else if (task.Manual is { } item) EditTask(item);
                }));
                if (task.Recurring is { } recurring)
                    actions.Children.Add(Button("Серия", () => EditRecurrence(store.All<Recurrence>().FirstOrDefault(rule => rule.Id == recurring.SeriesId))));
                if (task.WorkoutDayId is { } workoutDayId)
                {
                    var session = scheduledSessionId is { } id ? store.All<TrainingLog>().FirstOrDefault(log => log.Id == id) : null;
                    var label = session is null ? "Начать тренировку" : session.Finished ? "Результат" : "Продолжить";
                    actions.Children.Add(Button(label, () => StartScheduledSession(workoutDayId, task.Manual, task.Recurring), primary: true));
                }
                panel.Children.Add(actions);
            }
            panel.Children.Add(Button("+ Дело", () => EditTask(new PlanItem(Guid.NewGuid(), string.Empty, date))));
            var card = Card(panel);
            card.Width = 128;
            card.MinHeight = monthView ? 190 : 240;
            card.Margin = new Avalonia.Thickness(0, 0, 8, 8);
            calendar.Children.Add(card);
        }
        body.Children.Add(calendar);
    }

    private void ShiftPeriod(int amount)
    {
        anchor = monthView ? anchor.AddMonths(amount) : anchor.AddDays(7 * amount);
        RenderPlanner();
    }

    private void EditTask(PlanItem item)
    {
        var title = Input("Название дела", item.Title);
        var date = DatePicker(item.Date);
        var time = Input("Время, например 12:00", item.Time);
        var feedback = Text(string.Empty, 12, muted: true);
        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Изменение действует для выбранного дела.", 14, muted: true),
                Text("Название", 13), title,
                Text("Дата", 13), date,
                Text("Точное время, если оно нужно для напоминания", 13), time,
                feedback
            }
        };
        var dialog = Dialog(item.Title.Length == 0 ? "Новое дело" : "Изменить дело", 620, 570, form);
        var actions = Row();
        actions.Children.Add(Button("Сохранить", () =>
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { feedback.Text = "Введите название дела."; return; }
            if (!string.IsNullOrWhiteSpace(time.Text) && !TimeOnly.TryParse(time.Text, Russian, out _))
            { feedback.Text = "Время укажите в формате 12:00."; return; }
            store.Save(item.Id, item with
            {
                Title = title.Text.Trim(),
                Date = PickedDate(date, item.Date),
                Time = string.IsNullOrWhiteSpace(time.Text) ? null : TimeOnly.Parse(time.Text, Russian).ToString("HH:mm")
            });
            dialog.Close();
            RenderPlanner();
        }, primary: true));
        if (store.All<PlanItem>().Any(existing => existing.Id == item.Id))
            actions.Children.Add(Button("Удалить", () =>
            {
                dialog.Close();
                ConfirmDestructive("Удалить дело?", $"«{item.Title}» исчезнет из плана.", "Удалить дело", () =>
                {
                    store.Delete<PlanItem>(item.Id);
                    RenderPlanner();
                });
            }));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void EditOccurrence(PlannedEntry occurrence)
    {
        var date = DatePicker(occurrence.Date);
        var done = new CheckBox { Content = "Выполнено", IsChecked = occurrence.Done };
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(occurrence.Title, 20),
                Text("Изменения относятся к этому вхождению. Правило серии не меняется.", 14, muted: true),
                Text("Дата в календаре", 13), date,
                done
            }
        };
        var dialog = Dialog("Вхождение повтора", 580, 440, form);
        var actions = Row();
        actions.Children.Add(Button("Сохранить", () =>
        {
            SaveOccurrence(occurrence, PickedDate(date, occurrence.Date), done.IsChecked == true);
            dialog.Close();
            RenderPlanner();
        }, primary: true));
        actions.Children.Add(Button("Пропустить", () =>
        {
            SaveOccurrence(occurrence, skipped: true);
            dialog.Close();
            RenderPlanner();
        }));
        actions.Children.Add(Button("Изменить серию", () =>
        {
            dialog.Close();
            EditRecurrence(store.All<Recurrence>().FirstOrDefault(rule => rule.Id == occurrence.SeriesId));
        }));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void SaveOccurrence(PlannedEntry occurrence, DateOnly? moveTo = null, bool? done = null, bool skipped = false)
    {
        if (occurrence.SeriesId is not { } seriesId || occurrence.OriginalDate is not { } originalDate) return;
        var id = RecurrenceProjection.OccurrenceKey(seriesId, originalDate);
        var current = store.All<OccurrenceOverride>().FirstOrDefault(item => item.Id == id);
        store.Save(id, new OccurrenceOverride(id, seriesId, originalDate,
            moveTo is { } moved && moved != originalDate ? moved : moveTo is null ? current?.MovedTo : null,
            done ?? current?.Done, skipped));
    }

    private void ShowRecurrenceManager()
    {
        Heading(body, "Повторы", "Правила автоматически создают дела в календаре. Отпуск может исключать выбранную категорию.");
        var toolbar = Row();
        toolbar.Children.Add(Button("← К календарю", RenderPlanner));
        toolbar.Children.Add(Button("Создать повтор", () => EditRecurrence(null), primary: true));
        body.Children.Add(toolbar);

        var rules = store.All<Recurrence>().OrderBy(item => item.StartsOn).ToArray();
        if (rules.Length == 0) body.Children.Add(Card(Text("Повторов пока нет. Создайте ежедневное, недельное или месячное правило.", 14, muted: true)));
        foreach (var rule in rules)
        {
            var actions = Row();
            actions.Children.Add(Button("Изменить", () => EditRecurrence(rule)));
            actions.Children.Add(Button("Удалить правило", () =>
            {
                ConfirmDestructive("Удалить правило повтора?",
                    $"«{rule.Title}» исчезнет из календаря. Индивидуальные переносы, статусы и пропуски этой серии также будут удалены.",
                    "Удалить серию", () =>
                    {
                        foreach (var occurrenceOverride in store.All<OccurrenceOverride>().Where(item => item.SeriesId == rule.Id))
                            store.Delete<OccurrenceOverride>(occurrenceOverride.Id);
                        store.Delete<Recurrence>(rule.Id);
                        ShowRecurrenceManager();
                    });
            }));
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    Text(rule.Title, 19),
                    Text(RecurrenceDescription(rule), 13, muted: true),
                    Text($"Начало: {rule.StartsOn:dd.MM.yyyy}{(rule.EndsOn is { } end ? $" · до {end:dd.MM.yyyy}" : " · без даты окончания")}{(string.IsNullOrWhiteSpace(rule.Time) ? string.Empty : $" · {rule.Time}")}", 12, muted: true),
                    Text(string.IsNullOrWhiteSpace(rule.ExclusionCategory) ? "Не исключает периоды отсутствия" : $"Пропускается при категории отсутствия «{rule.ExclusionCategory}»", 12, muted: true),
                    actions
                }
            }));
        }

        var skipped = store.All<OccurrenceOverride>().Where(item => item.Skipped)
            .Select(item => (Override: item, Rule: rules.FirstOrDefault(rule => rule.Id == item.SeriesId)))
            .Where(item => item.Rule is not null)
            .OrderBy(item => item.Override.OriginalDate)
            .ToArray();
        body.Children.Add(Text("Пропущенные вхождения", 21));
        if (skipped.Length == 0)
            body.Children.Add(Text("Пропущенных вхождений нет.", 13, muted: true));
        foreach (var entry in skipped)
        {
            var occurrence = entry.Override;
            var rule = entry.Rule!;
            var restore = Button("Вернуть в календарь", () =>
            {
                store.Save(occurrence.Id, occurrence with { Skipped = false });
                ShowRecurrenceManager();
            });
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text(rule.Title, 15),
                    Text($"Исходная дата: {occurrence.OriginalDate:dd.MM.yyyy}", 12, muted: true),
                    restore
                }
            }));
        }
    }

    private void EditRecurrence(Recurrence? existing)
    {
        var original = existing ?? new Recurrence(Guid.NewGuid(), Guid.NewGuid(), string.Empty, null,
            anchor, RecurrenceFrequency.Weekly, 1, [DayOfWeek.Monday], [], null, null);
        var title = Input("Название дела", original.Title);
        var start = DatePicker(original.StartsOn);
        var hasEnd = new CheckBox { Content = "Остановить повтор после даты", IsChecked = original.EndsOn is not null };
        var end = DatePicker(original.EndsOn ?? original.StartsOn.AddMonths(3));
        var time = Input("Время, например 12:00 (необязательно)", original.Time);
        var interval = Input("Каждые N периодов", original.Interval.ToString(CultureInfo.InvariantCulture));
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Каждый день", "По выбранным дням недели", "По выбранным числам месяца" },
            SelectedIndex = original.Frequency switch { RecurrenceFrequency.Daily => 0, RecurrenceFrequency.Monthly => 2, _ => 1 },
            MinHeight = 42
        };
        var weekdayRows = Enum.GetValues<DayOfWeek>()
            .Where(day => day != DayOfWeek.Sunday)
            .Append(DayOfWeek.Sunday)
            .Select(day => (Day: day, Check: new CheckBox { Content = Russian.DateTimeFormat.GetDayName(day), IsChecked = original.Weekdays.Contains(day) }))
            .ToArray();
        var weekdayPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var entry in weekdayRows) weekdayPanel.Children.Add(entry.Check);
        var presetButtons = Row(6);
        presetButtons.Children.Add(Button("Будни", () => SetWeekdays(weekdayRows, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday])));
        presetButtons.Children.Add(Button("Выходные", () => SetWeekdays(weekdayRows, [DayOfWeek.Saturday, DayOfWeek.Sunday])));
        presetButtons.Children.Add(Button("Все дни", () => SetWeekdays(weekdayRows, Enum.GetValues<DayOfWeek>())));
        var monthDays = Input("Например: 1, 15, 28", string.Join(", ", original.MonthDays));
        var exclusion = Input("Категория отсутствия, которую нужно учитывать", original.ExclusionCategory);
        var workoutDays = store.All<TrainingDay>().OrderBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
        var workoutPicker = new ComboBox
        {
            ItemsSource = new[] { "Без тренировки" }.Concat(workoutDays.Select(item => item.Name)).ToArray(),
            SelectedIndex = original.WorkoutDayId is { } workoutId
                ? Math.Max(0, Array.FindIndex(workoutDays, item => item.Id == workoutId) + 1)
                : 0,
            MinHeight = 42
        };
        var validation = Text(string.Empty, 12, muted: true);
        var weekSection = new StackPanel { Spacing = 8, Children = { Text("Повторять каждую неделю или несколько недель", 13), weekdayPanel, presetButtons } };
        var monthSection = new StackPanel { Spacing = 8, Children = { Text("Числа месяца (через запятую)", 13), monthDays } };
        void SyncMode()
        {
            weekdayPanel.IsVisible = mode.SelectedIndex == 1;
            presetButtons.IsVisible = mode.SelectedIndex == 1;
            monthDays.IsVisible = mode.SelectedIndex == 2;
            weekSection.IsVisible = mode.SelectedIndex == 1;
            monthSection.IsVisible = mode.SelectedIndex == 2;
        }
        mode.SelectionChanged += (_, _) => SyncMode();
        SyncMode();

        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Правило повтора", 21),
                Text("Заголовок дела", 13), title,
                Text("Периодичность", 13), mode,
                Text("Интервал", 13), interval,
                weekSection, monthSection,
                Text("Первый день правила", 13), start,
                hasEnd, end,
                Text("Время по Москве", 13), time,
                Text("Исключать совпадающие отпуска по категории", 13), exclusion,
                Text("Категории отпусков сейчас: " + string.Join(", ", store.All<Vacation>().Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase)), 11, muted: true),
                Text("Связать повтор с тренировочным днём (необязательно)", 13), workoutPicker,
                validation
            }
        };
        var dialog = Dialog(existing is null ? "Новое правило" : "Изменить правило", 700, 830, form);
        var actions = Row();
        actions.Children.Add(Button("Сохранить правило", () =>
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { validation.Text = "Введите название дела."; return; }
            if (!int.TryParse(interval.Text, out var every) || every is < 1 or > 365) { validation.Text = "Интервал должен быть от 1 до 365."; return; }
            if (!string.IsNullOrWhiteSpace(time.Text) && !TimeOnly.TryParse(time.Text, Russian, out _)) { validation.Text = "Время укажите в формате 12:00."; return; }

            var frequency = mode.SelectedIndex switch { 0 => RecurrenceFrequency.Daily, 2 => RecurrenceFrequency.Monthly, _ => RecurrenceFrequency.Weekly };
            var weekdays = frequency == RecurrenceFrequency.Weekly
                ? weekdayRows.Where(row => row.Check.IsChecked == true).Select(row => row.Day).ToArray()
                : [];
            var days = frequency == RecurrenceFrequency.Monthly
                ? monthDays.Text?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, out var parsed) ? parsed : -1).ToArray() ?? []
                : [];
            if (frequency == RecurrenceFrequency.Weekly && weekdays.Length == 0) { validation.Text = "Выберите хотя бы один день недели."; return; }
            if (frequency == RecurrenceFrequency.Monthly && (days.Length == 0 || days.Any(day => day is < 1 or > 31)))
            { validation.Text = "Укажите числа месяца от 1 до 31 через запятую."; return; }
            var begins = PickedDate(start, original.StartsOn);
            DateOnly? ends = hasEnd.IsChecked == true ? PickedDate(end, original.EndsOn ?? begins) : null;
            if (ends < begins) { validation.Text = "Дата окончания должна быть не раньше даты начала."; return; }

            var recurrence = original with
            {
                Title = title.Text.Trim(),
                Time = string.IsNullOrWhiteSpace(time.Text) ? null : TimeOnly.Parse(time.Text, Russian).ToString("HH:mm"),
                StartsOn = begins,
                Frequency = frequency,
                Interval = every,
                Weekdays = weekdays,
                MonthDays = days.Distinct().Order().ToArray(),
                EndsOn = ends,
                ExclusionCategory = string.IsNullOrWhiteSpace(exclusion.Text) ? null : exclusion.Text.Trim(),
                WorkoutDayId = workoutPicker.SelectedIndex > 0 && workoutPicker.SelectedIndex <= workoutDays.Length
                    ? workoutDays[workoutPicker.SelectedIndex - 1].Id
                    : null
            };
            try { RecurrenceProjection.ToRule(recurrence).Validate(); }
            catch (ArgumentException exception) { validation.Text = exception.Message; return; }
            store.Save(recurrence.Id, recurrence);
            dialog.Close();
            RenderPlanner();
        }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void ShowVacationManager()
    {
        Heading(body, "Отпуска и периоды отсутствия", "Для выбранной категории повторяющиеся дела автоматически пропускаются. Пересекающиеся периоды разрешены.");
        var actions = Row();
        actions.Children.Add(Button("← К календарю", RenderPlanner));
        actions.Children.Add(Button("Добавить период", () => EditVacation(null), primary: true));
        body.Children.Add(actions);
        var vacations = store.All<Vacation>().OrderBy(item => item.Start).ToArray();
        if (vacations.Length == 0) body.Children.Add(Card(Text("Периоды отсутствия пока не заданы.", 14, muted: true)));
        foreach (var vacation in vacations)
        {
            var linkedRules = store.All<Recurrence>().Count(rule => rule.ExclusionCategory == vacation.Category);
            var actionsForItem = Row();
            actionsForItem.Children.Add(Button("Изменить", () => EditVacation(vacation)));
            actionsForItem.Children.Add(Button("Удалить", () => ConfirmDestructive("Удалить период отсутствия?",
                $"Повторы с категорией «{vacation.Category}» снова появятся на эти даты, если правило их задаёт.",
                "Удалить период", () => { store.Delete<Vacation>(vacation.Id); ShowVacationManager(); })));
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text(vacation.Title, 18),
                    Text($"{vacation.Start:dd.MM.yyyy} — {vacation.End:dd.MM.yyyy} · категория «{vacation.Category}»", 13, muted: true),
                    Text(linkedRules > 0
                        ? $"Связанных правил повтора: {linkedRules}" : "Нет правил с такой категорией исключения", 12, muted: true),
                    actionsForItem
                }
            }));
        }
    }

    private void EditVacation(Vacation? existing)
    {
        var original = existing ?? new Vacation(Guid.NewGuid(), string.Empty, anchor, anchor, "Отсутствие");
        var title = Input("Название периода", original.Title);
        var category = Input("Категория (например: личный отпуск)", original.Category);
        var start = DatePicker(original.Start);
        var end = DatePicker(original.End);
        var feedback = Text(string.Empty, 12, muted: true);
        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Период отсутствия", 21),
                Text("Название", 13), title,
                Text("Категория для связи с повторами", 13), category,
                Text("Начало", 13), start,
                Text("Последний день включительно", 13), end,
                Text("Совпадающее название категории в правиле повторов пропустит событие в эти даты.", 12, muted: true),
                feedback
            }
        };
        var dialog = Dialog(existing is null ? "Добавить период" : "Изменить период", 600, 600, form);
        var actions = Row();
        actions.Children.Add(Button("Сохранить", () =>
        {
            var begins = PickedDate(start, original.Start);
            var ends = PickedDate(end, original.End);
            if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(category.Text))
            { feedback.Text = "Заполните название и категорию."; return; }
            if (ends < begins) { feedback.Text = "Дата окончания должна быть не раньше даты начала."; return; }
            store.Save(original.Id, original with { Title = title.Text.Trim(), Category = category.Text.Trim(), Start = begins, End = ends });
            dialog.Close();
            ShowVacationManager();
        }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private static string RecurrenceDescription(Recurrence rule) => rule.Frequency switch
    {
        RecurrenceFrequency.Daily => rule.Interval == 1 ? "Каждый день" : $"Каждые {rule.Interval} дней",
        RecurrenceFrequency.Weekly => $"Каждые {rule.Interval} нед. · {string.Join(", ", rule.Weekdays.OrderBy(day => ((int)day + 6) % 7).Select(Russian.DateTimeFormat.GetDayName))}",
        RecurrenceFrequency.Monthly => $"Каждые {rule.Interval} мес. · числа {string.Join(", ", rule.MonthDays.Order())}",
        _ => "Повтор"
    };

    private static void SetWeekdays(IEnumerable<(DayOfWeek Day, CheckBox Check)> rows, IEnumerable<DayOfWeek> selected)
    {
        var values = selected.ToHashSet();
        foreach (var row in rows) row.Check.IsChecked = values.Contains(row.Day);
    }
}
