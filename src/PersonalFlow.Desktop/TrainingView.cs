using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using PersonalFlow.Client;
using PersonalFlow.Core;

namespace PersonalFlow.Desktop;

public sealed partial class FlowWindow
{
    private void ShowTraining()
    {
        Heading(body, "Тренировки", "Структурированные планы, подходы, фактические результаты и история.");
        var toolbar = Wrap();
        toolbar.Children.Add(Button("Создать день программы", () => EditWorkout(null), primary: true));
        body.Children.Add(toolbar);

        var days = store.All<TrainingDay>().OrderBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
        if (days.Length == 0)
            body.Children.Add(Card(Text("Пока нет плана. Создайте тренировочный день и добавьте упражнения с целями для каждого подхода.", 14, muted: true)));

        foreach (var day in days)
        {
            var template = WorkoutDocuments.ReadPlan(day.Id, day.Name, day.Exercises);
            var active = store.All<TrainingLog>().Any(log => log.DayId == day.Id && !log.Finished);
            var exercises = new StackPanel { Spacing = 7 };
            foreach (var exercise in template.Exercises)
            {
                var targets = string.Join("  ·  ", exercise.Sets.Select((set, index) => $"{index + 1}: {WorkoutDocuments.FormatTarget(set)}"));
                exercises.Children.Add(Text(exercise.Name, 15));
                exercises.Children.Add(Text(targets, 12, muted: true));
            }
            var actions = Wrap();
            actions.Children.Add(Button("Изменить структуру", () => EditWorkout(day)));
            actions.Children.Add(Button("Создать копию", () => DuplicateWorkout(day)));
            actions.Children.Add(Button("Запланировать", () => ScheduleWorkout(day)));
            actions.Children.Add(Button(active ? "Продолжить тренировку ниже" : "Начать тренировку", () => StartSession(day), primary: !active));
            actions.Children.Add(Button("Удалить", () => DeleteWorkout(day)));
            body.Children.Add(Card(new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Text(day.Name, 21),
                    Text($"{template.Exercises.Count} упражнений · {template.Exercises.Sum(exercise => exercise.Sets.Count)} целевых подходов", 12, muted: true),
                    exercises,
                    actions
                }
            }));
        }

        var sessions = store.All<TrainingLog>().OrderByDescending(log => log.Started).ToArray();
        body.Children.Add(Text("Тренировки", 23));
        if (sessions.Length == 0) body.Children.Add(Text("Здесь появятся начатые и завершённые занятия.", 13, muted: true));
        foreach (var session in sessions) RenderSession(session);
    }

    private void EditWorkout(TrainingDay? existing)
    {
        var originalTemplate = existing is null
            ? new WorkoutTemplate(Guid.NewGuid(), string.Empty,
                [new ExerciseTemplate(Guid.NewGuid(), string.Empty, [new SetTarget(Guid.NewGuid(), 10, null, "кг", null)])])
            : WorkoutDocuments.ReadPlan(existing.Id, existing.Name, existing.Exercises);
        var name = Input("Например: День A", existing?.Name ?? string.Empty);
        var exercisePanel = new StackPanel { Spacing = 12 };
        var exercises = WorkoutDocuments.ToDrafts(originalTemplate);
        if (exercises.Count == 0) exercises.Add(new WorkoutExerciseDraft());

        void RenderExercises()
        {
            exercisePanel.Children.Clear();
            for (var index = 0; index < exercises.Count; index++)
            {
                var position = index;
                var draft = exercises[position];
                var exerciseName = Input("Название упражнения", draft.Name);
                var note = Input("Техника, оборудование или комментарий", draft.Note);
                var setPanel = new StackPanel { Spacing = 6 };
                var setEditors = new List<(WorkoutSetDraft Draft, TextBox Repetitions, TextBox Load, TextBox Unit)>();

                void RenderSets()
                {
                    setPanel.Children.Clear();
                    setEditors.Clear();
                    foreach (var set in draft.Sets)
                    {
                        var repetitions = Input("Повторы", set.Repetitions);
                        var load = Input("Вес", set.Load);
                        var unit = Input("Ед.", set.Unit);
                        repetitions.Width = 115;
                        load.Width = 120;
                        unit.Width = 90;
                        setEditors.Add((set, repetitions, load, unit));
                        var row = Row(6);
                        row.Children.Add(Text("Подход", 12, muted: true));
                        row.Children.Add(repetitions);
                        row.Children.Add(load);
                        row.Children.Add(unit);
                        row.Children.Add(Button("Удалить подход", () =>
                        {
                            if (draft.Sets.Count == 1) return;
                            draft.Sets.Remove(set);
                            RenderSets();
                        }));
                        setPanel.Children.Add(row);
                    }
                }

                RenderSets();
                var exerciseActions = Row(6);
                exerciseActions.Children.Add(Button("+ Подход", () =>
                {
                    draft.Sets.Add(new WorkoutSetDraft { Repetitions = "10", Unit = "кг" });
                    RenderSets();
                }));
                exerciseActions.Children.Add(Button("↑", () => MoveExercise(position, -1)));
                exerciseActions.Children.Add(Button("↓", () => MoveExercise(position, 1)));
                if (exercises.Count > 1)
                    exerciseActions.Children.Add(Button("Удалить упражнение", () => { exercises.RemoveAt(position); RenderExercises(); }));

                exercisePanel.Children.Add(Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Text($"Упражнение {position + 1}", 15),
                        exerciseName,
                        note,
                        Text("Цели подходов", 12, muted: true),
                        setPanel,
                        exerciseActions
                    }
                }));

                // Read the visible controls when the Save button is pressed. The closure retains one control set per exercise card.
                draft.Name = exerciseName.Text ?? string.Empty;
                draft.Note = note.Text ?? string.Empty;
                exerciseName.TextChanged += (_, _) => draft.Name = exerciseName.Text ?? string.Empty;
                note.TextChanged += (_, _) => draft.Note = note.Text ?? string.Empty;
                foreach (var entry in setEditors)
                {
                    entry.Repetitions.TextChanged += (_, _) => entry.Draft.Repetitions = entry.Repetitions.Text ?? string.Empty;
                    entry.Load.TextChanged += (_, _) => entry.Draft.Load = entry.Load.Text ?? string.Empty;
                    entry.Unit.TextChanged += (_, _) => entry.Draft.Unit = entry.Unit.Text ?? string.Empty;
                }
            }
        }

        void MoveExercise(int index, int delta)
        {
            var destination = index + delta;
            if (destination < 0 || destination >= exercises.Count) return;
            (exercises[index], exercises[destination]) = (exercises[destination], exercises[index]);
            RenderExercises();
        }

        RenderExercises();
        var feedback = Text(string.Empty, 12, muted: true);
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(existing is null ? "Новый тренировочный день" : "Редактор программы", 22),
                Text("Название дня", 13), name,
                Text("Упражнения можно переставлять. Каждый подход хранит отдельную цель повторов и нагрузки.", 13, muted: true),
                exercisePanel,
                Button("+ Добавить упражнение", () => { exercises.Add(new WorkoutExerciseDraft()); RenderExercises(); }),
                feedback
            }
        };
        var dialog = Dialog("Программа тренировок", 940, 850, form);
        var actions = Row();
        actions.Children.Add(Button("Сохранить программу", () =>
        {
            foreach (var exercise in exercises)
            {
                // Draft objects are updated on every text change, including before this action.
                if (exercise.Sets.Count == 0) exercise.Sets.Add(new WorkoutSetDraft());
            }
            if (!WorkoutDocuments.TryBuildTemplate(originalTemplate.Id, name.Text ?? string.Empty, exercises,
                    out var template, out var error))
            {
                feedback.Text = error;
                return;
            }
            var id = existing?.Id ?? template!.Id;
            template = template! with { Id = id };
            store.Save(id, new TrainingDay(id, template.Name, WorkoutDocuments.WritePlan(template)));
            dialog.Close();
            ShowTraining();
        }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void DuplicateWorkout(TrainingDay source)
    {
        var template = WorkoutDocuments.ReadPlan(source.Id, source.Name, source.Exercises);
        var id = Guid.NewGuid();
        var copy = template with
        {
            Id = id,
            Name = $"{template.Name} — копия",
            Exercises = template.Exercises.Select(exercise => exercise with
            {
                Id = Guid.NewGuid(),
                Sets = exercise.Sets.Select(set => set with { Id = Guid.NewGuid() }).ToArray()
            }).ToArray()
        };
        store.Save(id, new TrainingDay(id, copy.Name, WorkoutDocuments.WritePlan(copy)));
        ShowTraining();
    }

    private void ScheduleWorkout(TrainingDay day)
    {
        var mode = new ComboBox { ItemsSource = new[] { "Один раз", "Еженедельно" }, SelectedIndex = 0, MinHeight = 42 };
        var start = DatePicker(LocalStore.Today);
        var hasEnd = new CheckBox { Content = "Закончить повтор после даты" };
        var end = DatePicker(LocalStore.Today.AddMonths(3));
        var time = Input("Время по Москве (необязательно)");
        var interval = Input("Каждые N недель", "1");
        var category = Input("Категория отпуска, если повтор нужно пропускать");
        var weekdayRows = Enum.GetValues<DayOfWeek>()
            .OrderBy(weekday => (int)(weekday + 6) % 7)
            .Select(weekday => (Day: weekday, Check: new CheckBox { Content = Russian.DateTimeFormat.GetDayName(weekday), IsChecked = weekday == LocalStore.Today.DayOfWeek }))
            .ToArray();
        var weekdays = Wrap();
        foreach (var row in weekdayRows) weekdays.Children.Add(row.Check);
        var presets = Wrap();
        presets.Children.Add(Button("Будни", () => SetWorkoutWeekdays(weekdayRows, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday])));
        presets.Children.Add(Button("Выходные", () => SetWorkoutWeekdays(weekdayRows, [DayOfWeek.Saturday, DayOfWeek.Sunday])));
        presets.Children.Add(Button("Все дни", () => SetWorkoutWeekdays(weekdayRows, Enum.GetValues<DayOfWeek>())));
        var weeklyFields = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Дни недели", 13), weekdays, presets,
                Text("Интервал", 13), interval,
                hasEnd, end,
                Text("Повтор будет пропущен, если категория совпадёт с отпуском.", 12, muted: true), category
            }
        };
        void UpdateMode()
        {
            weeklyFields.IsVisible = mode.SelectedIndex == 1;
        }
        mode.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();

        var feedback = Text(string.Empty, 12, muted: true);
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text($"Добавить «{day.Name}» в календарь", 21),
                Text("Разовое занятие или недельное расписание. Изменение программы не перепишет снимок уже начатой тренировки.", 13, muted: true),
                Text("Тип расписания", 13), mode,
                Text("Начальная дата", 13), start,
                weeklyFields,
                Text("Время начала по Москве", 13), time,
                feedback
            }
        };
        var dialog = Dialog("Запланировать тренировку", 700, 760, content);
        var actions = Wrap();
        actions.Children.Add(Button("Добавить в план", () =>
        {
            var begins = PickedDate(start, LocalStore.Today);
            if (!string.IsNullOrWhiteSpace(time.Text) && !TimeOnly.TryParse(time.Text, Russian, out _))
            { feedback.Text = "Время укажите в формате 12:00."; return; }
            var normalizedTime = string.IsNullOrWhiteSpace(time.Text) ? null : TimeOnly.Parse(time.Text, Russian).ToString("HH:mm");
            if (mode.SelectedIndex != 1)
            {
                var id = Guid.NewGuid();
                store.Save(id, new PlanItem(id, $"Тренировка · {day.Name}", begins, Time: normalizedTime, WorkoutDayId: day.Id));
            }
            else
            {
                if (!int.TryParse(interval.Text, out var every) || every is < 1 or > 52)
                { feedback.Text = "Интервал должен быть от 1 до 52 недель."; return; }
                var selectedDays = weekdayRows.Where(row => row.Check.IsChecked == true).Select(row => row.Day).ToArray();
                if (selectedDays.Length == 0) { feedback.Text = "Выберите хотя бы один день недели."; return; }
                DateOnly? ends = hasEnd.IsChecked == true ? PickedDate(end, begins) : null;
                if (ends < begins) { feedback.Text = "Дата окончания должна быть не раньше начала."; return; }
                var rule = new Recurrence(Guid.NewGuid(), Guid.NewGuid(), $"Тренировка · {day.Name}", normalizedTime,
                    begins, RecurrenceFrequency.Weekly, every, selectedDays, [], ends,
                    string.IsNullOrWhiteSpace(category.Text) ? null : category.Text.Trim(), day.Id);
                store.Save(rule.Id, rule);
            }
            dialog.Close();
            ShowTraining();
        }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        content.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private static void SetWorkoutWeekdays(IEnumerable<(DayOfWeek Day, CheckBox Check)> rows, IEnumerable<DayOfWeek> selected)
    {
        var values = selected.ToHashSet();
        foreach (var row in rows) row.Check.IsChecked = values.Contains(row.Day);
    }

    private void DeleteWorkout(TrainingDay day)
    {
        var dialog = Dialog("Удалить программу?", 560, 320, new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(day.Name, 20),
                Text("История сохранится со снимками программы. Запланированные даты останутся в календаре как обычные дела без связи с удалённым планом.", 13, muted: true)
            }
        });
        var content = (StackPanel)((ScrollViewer)dialog.Content!).Content!;
        var actions = Row();
        actions.Children.Add(Button("Удалить программу", () => { store.DeleteWorkoutTemplate(day.Id); dialog.Close(); ShowTraining(); }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        content.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void StartSession(TrainingDay day)
    {
        if (store.All<TrainingLog>().Any(log => log.DayId == day.Id && !log.Finished))
        {
            ShowTraining();
            return;
        }
        var template = WorkoutDocuments.ReadPlan(day.Id, day.Name, day.Exercises);
        var id = Guid.NewGuid();
        store.Save(id, new TrainingLog(id, day.Id, day.Name, WorkoutDocuments.WritePlan(template), DateTimeOffset.UtcNow));
        ShowTraining();
    }

    private void StartScheduledSession(Guid workoutDayId, PlanItem? scheduledItem, PlannedEntry? occurrence)
    {
        var day = store.All<TrainingDay>().FirstOrDefault(item => item.Id == workoutDayId);
        if (day is null) return;
        var sessionId = scheduledItem?.Id ?? occurrence?.Id ?? Guid.NewGuid();
        if (!store.All<TrainingLog>().Any(log => log.Id == sessionId))
        {
            var template = WorkoutDocuments.ReadPlan(day.Id, day.Name, day.Exercises);
            store.Save(sessionId, new TrainingLog(sessionId, day.Id, day.Name,
                WorkoutDocuments.WritePlan(template), DateTimeOffset.UtcNow,
                ScheduleItemId: scheduledItem?.Id,
                ScheduleSeriesId: occurrence?.SeriesId,
                ScheduleOriginalDate: occurrence?.OriginalDate));
        }
        ShowTraining();
    }

    private void RenderSession(TrainingLog log)
    {
        var template = WorkoutDocuments.ReadPlan(log.DayId, log.Name, log.Snapshot);
        var resultDocument = WorkoutDocuments.ReadLog(log.Results);
        var sessionNotes = Input("Заметка к занятию", resultDocument.Notes, multiline: true);
        sessionNotes.IsEnabled = !log.Finished;
        var saveState = Text(log.Finished ? "Завершённая тренировка доступна для просмотра." : "Изменения сохраняются автоматически после короткой паузы.", 12, muted: true);
        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        void ScheduleSave()
        {
            if (log.Finished) return;
            saveState.Text = "Сохраню изменения на устройстве…";
            saveTimer.Stop();
            saveTimer.Start();
        }
        var rows = new List<(SetTarget Target, TextBox Repetitions, TextBox Load, CheckBox Completed, CheckBox Skipped)>();
        var content = new StackPanel { Spacing = 9 };
        foreach (var exercise in template.Exercises)
        {
            content.Children.Add(Text(exercise.Name, 15));
            if (!string.IsNullOrWhiteSpace(exercise.Note)) content.Children.Add(Text(exercise.Note, 12, muted: true));
            foreach (var target in exercise.Sets)
            {
                var result = resultDocument.Sets.FirstOrDefault(item => item.SetId == target.Id);
                var reps = Input($"Факт повторы · цель {target.Repetitions?.ToString(CultureInfo.InvariantCulture) ?? "—"}", result?.Repetitions?.ToString(CultureInfo.InvariantCulture));
                var load = Input($"Факт нагрузка · цель {WorkoutDocuments.FormatTarget(target)}", result?.Load?.ToString(CultureInfo.InvariantCulture));
                reps.Width = 175;
                load.Width = 250;
                var completed = new CheckBox { Content = "Выполнен", IsChecked = result?.Completed == true, IsEnabled = !log.Finished };
                var skipped = new CheckBox
                {
                    Content = "Пропустить",
                    IsChecked = result is { Completed: false, Repetitions: null, Load: null },
                    IsEnabled = !log.Finished
                };
                completed.IsCheckedChanged += (_, _) => { if (completed.IsChecked == true) skipped.IsChecked = false; };
                skipped.IsCheckedChanged += (_, _) =>
                {
                    if (skipped.IsChecked != true) return;
                    completed.IsChecked = false;
                    reps.Text = string.Empty;
                    load.Text = string.Empty;
                };
                reps.IsEnabled = !log.Finished;
                load.IsEnabled = !log.Finished;
                rows.Add((target, reps, load, completed, skipped));
                var row = Row(8);
                row.Children.Add(Text("Подход", 12, muted: true));
                row.Children.Add(reps);
                row.Children.Add(load);
                row.Children.Add(completed);
                row.Children.Add(skipped);
                content.Children.Add(row);
                reps.TextChanged += (_, _) => ScheduleSave();
                load.TextChanged += (_, _) => ScheduleSave();
                completed.IsCheckedChanged += (_, _) => ScheduleSave();
                skipped.IsCheckedChanged += (_, _) => ScheduleSave();
            }
        }

        var status = log.Finished ? "Завершена" : "В процессе";
        var card = new StackPanel { Spacing = 10 };
        card.Children.Add(Text($"{log.Name} · {log.Started.ToLocalTime():dd MMM yyyy, HH:mm} · {status}", 19));
        card.Children.Add(Text("Цели и фактические значения хранятся отдельно. Незавершённое занятие можно продолжить после перезапуска.", 12, muted: true));
        card.Children.Add(content);
        if (!log.Finished)
            card.Children.Add(Button("Добавить упражнение только в это занятие", () => AddExerciseToSession(log, template)));
        card.Children.Add(Text("Заметка к тренировке", 13));
        card.Children.Add(sessionNotes);
        card.Children.Add(saveState);
        var actions = Row();
        if (!log.Finished)
            actions.Children.Add(Button("Сохранить результаты", () => SaveSession(log, resultDocument, sessionNotes, rows, content, saveTimer, saveState, finish: false, refresh: true)));
        if (!log.Finished)
            actions.Children.Add(Button("Завершить тренировку", () => SaveSession(log, resultDocument, sessionNotes, rows, content, saveTimer, saveState, finish: true, refresh: true), primary: true));
        else
            actions.Children.Add(Text("Завершённая тренировка открыта только для просмотра.", 12, muted: true));
        card.Children.Add(actions);
        sessionNotes.TextChanged += (_, _) => ScheduleSave();
        saveTimer.Tick += (_, _) => SaveSession(log, resultDocument, sessionNotes, rows, content, saveTimer, saveState, finish: false, refresh: false);
        body.Children.Add(Card(card));
    }

    private void AddExerciseToSession(TrainingLog log, WorkoutTemplate snapshot)
    {
        var name = Input("Название упражнения");
        var setCount = Input("Количество подходов", "3");
        var repetitions = Input("Целевые повторы", "10");
        var load = Input("Целевая нагрузка (необязательно)");
        var unit = Input("Единица нагрузки", "кг");
        var note = Input("Заметка или свободная цель", multiline: true);
        var feedback = Text(string.Empty, 12, muted: true);
        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Дополнительное упражнение", 21),
                Text("Оно сохранится только в снимке этой тренировки и не изменит программу.", 13, muted: true),
                name,
                setCount,
                repetitions,
                load,
                unit,
                note,
                feedback
            }
        };
        var dialog = Dialog("Добавить упражнение", 620, 650, form);
        var actions = Row();
        actions.Children.Add(Button("Добавить", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { feedback.Text = "Введите название упражнения."; return; }
            if (!int.TryParse(setCount.Text, out var count) || count is < 1 or > 30)
            { feedback.Text = "Количество подходов должно быть от 1 до 30."; return; }
            int? reps = null;
            if (!string.IsNullOrWhiteSpace(repetitions.Text))
            {
                if (!int.TryParse(repetitions.Text, out var parsedReps) || parsedReps is < 1 or > 1000)
                { feedback.Text = "Повторы должны быть целым числом от 1 до 1000."; return; }
                reps = parsedReps;
            }
            decimal? weight = null;
            if (!string.IsNullOrWhiteSpace(load.Text))
            {
                if (!decimal.TryParse(load.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedLoad) || parsedLoad is < 0 or > 100_000)
                { feedback.Text = "Проверьте нагрузку."; return; }
                weight = parsedLoad;
            }
            var targetNote = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim();
            if (reps is null && weight is null && targetNote is null)
            { feedback.Text = "Укажите цель упражнения: повторы, нагрузку или заметку."; return; }
            var sets = Enumerable.Range(0, count)
                .Select(_ => new SetTarget(Guid.NewGuid(), reps, weight,
                    string.IsNullOrWhiteSpace(unit.Text) ? null : unit.Text.Trim(), targetNote))
                .ToArray();
            var extra = new ExerciseTemplate(Guid.NewGuid(), name.Text.Trim(), sets);
            var updated = snapshot with { Exercises = snapshot.Exercises.Append(extra).ToArray() };
            store.Save(log.Id, log with { Snapshot = WorkoutDocuments.WritePlan(updated) });
            dialog.Close();
            ShowTraining();
        }, primary: true));
        actions.Children.Add(Button("Отмена", () => dialog.Close()));
        form.Children.Add(actions);
        _ = ShowModalAsync(dialog);
    }

    private void SaveSession(TrainingLog log, WorkoutLogDocument original,
        TextBox notes, IReadOnlyList<(SetTarget Target, TextBox Repetitions, TextBox Load, CheckBox Completed, CheckBox Skipped)> rows,
        StackPanel sessionContent, DispatcherTimer saveTimer, TextBlock saveState, bool finish, bool refresh)
    {
        saveTimer.Stop();
        var results = new List<SetResult>();
        foreach (var row in rows)
        {
            int? repetitions = null;
            decimal? load = null;
            if (!string.IsNullOrWhiteSpace(row.Repetitions.Text))
            {
                if (!int.TryParse(row.Repetitions.Text, out var parsed) || parsed < 0)
                {
                    ShowInlineError(sessionContent, "Повторы должны быть целым числом от 0 и выше.");
                    return;
                }
                repetitions = parsed;
            }
            if (!string.IsNullOrWhiteSpace(row.Load.Text))
            {
                if (!decimal.TryParse(row.Load.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                {
                    ShowInlineError(sessionContent, "Нагрузка должна быть числом от 0 и выше.");
                    return;
                }
                load = parsed;
            }
            if (repetitions is not null || load is not null || row.Completed.IsChecked == true || row.Skipped.IsChecked == true)
                results.Add(new SetResult(row.Target.Id, repetitions, load, row.Completed.IsChecked == true && row.Skipped.IsChecked != true));
        }
        var document = new WorkoutLogDocument(1, results, notes.Text ?? original.Notes);
        var json = WorkoutDocuments.WriteLog(document);
        var current = store.All<TrainingLog>().FirstOrDefault(item => item.Id == log.Id) ?? log;
        if (!finish && string.Equals(current.Results, json, StringComparison.Ordinal))
        {
            saveState.Text = "Сохранено на устройстве";
            return;
        }
        var updated = current with
        {
            Results = json,
            Finished = current.Finished || finish
        };
        if (finish)
        {
            var planItem = current.ScheduleItemId is { } itemId
                ? store.All<PlanItem>().FirstOrDefault(item => item.Id == itemId)
                : null;
            if (planItem is { Done: false }) planItem = planItem with { Done = true };
            else planItem = null;

            OccurrenceOverride? occurrenceOverride = null;
            if (current.ScheduleSeriesId is { } seriesId && current.ScheduleOriginalDate is { } originalDate)
            {
                var overrideId = RecurrenceProjection.OccurrenceKey(seriesId, originalDate);
                var existingOverride = store.All<OccurrenceOverride>().FirstOrDefault(item => item.Id == overrideId);
                if (existingOverride?.Done != true)
                    occurrenceOverride = new OccurrenceOverride(overrideId, seriesId, originalDate,
                        existingOverride?.MovedTo, true, existingOverride?.Skipped ?? false);
            }
            store.CompleteWorkout(updated, planItem, occurrenceOverride);
        }
        else store.Save(log.Id, updated);
        if (refresh) ShowTraining();
        else saveState.Text = "Сохранено на устройстве";
    }

    private static void ShowInlineError(StackPanel panel, string message)
    {
        var old = panel.Children.OfType<TextBlock>().FirstOrDefault(item => item.Tag as string == "validation");
        if (old is not null) panel.Children.Remove(old);
        panel.Children.Insert(0, new TextBlock { Text = message, Tag = "validation", Foreground = Avalonia.Media.Brush.Parse("#a03731"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
    }
}
