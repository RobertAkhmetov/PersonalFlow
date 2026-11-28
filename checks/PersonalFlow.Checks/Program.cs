using PersonalFlow.Client;
using PersonalFlow.Life.Api;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalFlow.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
var start = new DateOnly(2027, 1, 1);
var rule = new RecurrenceRule(start, RecurrenceFrequency.Weekly, 1,
    new HashSet<DayOfWeek> { DayOfWeek.Friday }, new HashSet<int>(),
    ExclusionCategory: "example");
var dates = rule.Expand(start, new DateOnly(2027, 1, 31),
    [new(new(2027, 1, 8), new(2027, 1, 15), "example")]);
Check(dates.SequenceEqual(new DateOnly[] { start, new(2027, 1, 22), new(2027, 1, 29) }),
    "Inclusive exclusion boundaries must skip both Fridays.");
var monthly = rule with
{
    Frequency = RecurrenceFrequency.Monthly,
    MonthDays = new HashSet<int> { 31 },
    ExclusionCategory = null
};
Check(monthly.Expand(start, new(2027, 3, 31), []).Count == 2,
    "Months without day 31 must be skipped.");
var workoutDayId = Guid.NewGuid();
var workoutSeries = new Recurrence(Guid.NewGuid(), Guid.NewGuid(), "Synthetic workout", "18:00", start,
    RecurrenceFrequency.Weekly, 1, [DayOfWeek.Friday], [], WorkoutDayId: workoutDayId);
var projectedWorkout = RecurrenceProjection.Expand(start, start.AddDays(8), [workoutSeries], [], []);
Check(projectedWorkout.Count == 2 && projectedWorkout.All(item => item.WorkoutDayId == workoutDayId),
    "Recurring workout links must survive projection.");
var legacyPlan = JsonSerializer.Deserialize<PlanItem>("{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Title\":\"Synthetic\",\"Date\":\"2027-01-01\",\"Done\":false,\"Time\":null}")!;
Check(legacyPlan.WorkoutDayId is null, "Old plan items must deserialize without a workout link.");
var task = new TaskOccurrence(new(Guid.NewGuid(), start), start, null, "Example", true, 1);
var moved = task.Move(start.AddDays(1));
Check(moved.Id == task.Id && moved.Completed, "Moving must retain identity and completion.");
Check(!FieldMerge.Resolve("a", "b", "a").HasConflict, "Independent change should merge.");
Check(FieldMerge.Resolve("a", "b", "c").HasConflict, "Divergent changes must conflict.");
Check(!FieldMerge.Resolve("a", "b", "b").HasConflict, "Equal changes converge.");
var setId = Guid.NewGuid();
var mutableSets = new List<SetTarget> { new(setId, 8, null, null, null) };
var template = new WorkoutTemplate(Guid.NewGuid(), "Synthetic",
    [new(Guid.NewGuid(), "Exercise", mutableSets)]);
var session = new WorkoutSession(Guid.NewGuid(), template, DateTimeOffset.UtcNow);
mutableSets.Clear();
Check(session.Snapshot.Exercises[0].Sets.Count == 1, "Session needs an independent snapshot.");
session.Record(new(setId, 7, null, true));
session.Record(new(setId, 6, null, true));
Check(session.Results.Count == 1, "Retrying a set update must not duplicate it.");
Console.WriteLine("Domain checks passed.");

var localPath=Path.Combine(Path.GetTempPath(),"personalflow-checks-"+Guid.NewGuid(),"client.db");
var store=new LocalStore(localPath);var planId=Guid.NewGuid();store.Save(planId,new PlanItem(planId,"Synthetic",start));
var reopened=new LocalStore(localPath);Check(reopened.All<PlanItem>().Single().Title=="Synthetic"&&reopened.Pending==1,"SQLite data and outbox survive reopening.");
reopened.Save(planId,reopened.All<PlanItem>().Single() with{Date=start.AddDays(1),Done=true});Check(reopened.All<PlanItem>().Count==1&&reopened.Pending==2,"Move preserves identity and records a new mutation.");
reopened.Delete<PlanItem>(planId);Check(reopened.All<PlanItem>().Count==0&&reopened.Pending==3,"Deletion and outgoing tombstone are atomic.");
Check(reopened.SyncStatus == new LocalSyncStatus(3, 0, 0), "Local sync diagnostics report queued operations without inventing conflicts.");
Console.WriteLine("5 SQLite persistence checks passed.");

var migrationPath = Path.Combine(Path.GetTempPath(), "personalflow-learning-migration-" + Guid.NewGuid(), "client.db");
var migrationStore = new LocalStore(migrationPath);
var oldLessonId = Guid.NewGuid();
migrationStore.Save(oldLessonId, new LessonAnswer(oldLessonId, "Legacy draft", Read: true));
var migratedProgress = migrationStore.GetLearningProgress(oldLessonId, 1);
Check(migratedProgress.Answer == "Legacy draft" && migratedProgress.Status == "Read", "Legacy lesson answers must migrate into versioned Learning progress.");
var reopenedMigration = new LocalStore(migrationPath);
Check(reopenedMigration.Pending == 1 && reopenedMigration.All<LessonAnswer>().Count == 0 && reopenedMigration.GetLearningProgress(oldLessonId, 1).Answer == "Legacy draft",
    "Learning migration must retire the unsupported Life outbox entry only after preserving the answer.");
var workoutStorePath = Path.Combine(Path.GetTempPath(), "personalflow-workout-checks-" + Guid.NewGuid(), "client.db");
var workoutStore = new LocalStore(workoutStorePath);
var scheduledWorkoutDayId = Guid.NewGuid();
var scheduledItemId = Guid.NewGuid();
var scheduledItem = new PlanItem(scheduledItemId, "Synthetic workout", start, WorkoutDayId: scheduledWorkoutDayId);
var scheduledLog = new TrainingLog(scheduledItemId, scheduledWorkoutDayId, "Synthetic workout", "{}", DateTimeOffset.UtcNow, ScheduleItemId: scheduledItemId);
workoutStore.Save(scheduledItemId, scheduledItem);
workoutStore.Save(scheduledLog.Id, scheduledLog);
var writesBeforeFinish = workoutStore.Pending;
workoutStore.CompleteWorkout(scheduledLog with { Finished = true }, scheduledItem with { Done = true }, null);
Check(workoutStore.All<TrainingLog>().Single().Finished && workoutStore.All<PlanItem>().Single().Done &&
      workoutStore.Pending == writesBeforeFinish + 2,
    "Finishing a scheduled workout must persist its result and calendar completion together.");

var weeklyLink = new Recurrence(Guid.NewGuid(), Guid.NewGuid(), "Synthetic weekly workout", null, start,
    RecurrenceFrequency.Weekly, 1, [DayOfWeek.Friday], [], WorkoutDayId: scheduledWorkoutDayId);
var occurrenceDate = new DateOnly(2027, 1, 1);
var occurrenceId = RecurrenceProjection.OccurrenceKey(weeklyLink.Id, occurrenceDate);
var recurringLog = new TrainingLog(occurrenceId, scheduledWorkoutDayId, weeklyLink.Title, "{}", DateTimeOffset.UtcNow,
    ScheduleSeriesId: weeklyLink.Id, ScheduleOriginalDate: occurrenceDate);
workoutStore.Save(weeklyLink.Id, weeklyLink);
workoutStore.Save(recurringLog.Id, recurringLog);
workoutStore.CompleteWorkout(recurringLog with { Finished = true }, null,
    new OccurrenceOverride(occurrenceId, weeklyLink.Id, occurrenceDate, Done: true));
Check(workoutStore.All<TrainingLog>().Single(item => item.Id == occurrenceId).Finished &&
      workoutStore.All<OccurrenceOverride>().Single().Done == true,
    "Finishing a recurring workout must complete its original calendar occurrence.");

var deletableDayId = Guid.NewGuid();
var remainingItemId = Guid.NewGuid();
workoutStore.Save(deletableDayId, new TrainingDay(deletableDayId, "Synthetic day", ""));
workoutStore.Save(remainingItemId, new PlanItem(remainingItemId, "Synthetic scheduled workout", start, WorkoutDayId: deletableDayId));
workoutStore.DeleteWorkoutTemplate(deletableDayId);
Check(workoutStore.All<TrainingDay>().All(item => item.Id != deletableDayId) &&
      workoutStore.All<PlanItem>().Single(item => item.Id == remainingItemId).WorkoutDayId is null,
    "Deleting a workout template must leave its calendar task without a broken link.");
Console.WriteLine("Scheduled workout persistence checks passed.");
var connection=Environment.GetEnvironmentVariable("CHECK_DATABASE");
if(connection is not null){
 var options=new DbContextOptionsBuilder<LifeDb>().UseNpgsql(connection).Options;
 var syncEpoch=Guid.NewGuid();await using(var db=new LifeDb(options)){await db.Database.EnsureCreatedAsync();db.SyncStates.Add(new LifeSyncState{Id="life",Epoch=syncEpoch});await db.SaveChangesAsync();}
 async Task Apply(Mutation m){await using var db=new LifeDb(options);await new SyncService(db).Apply(m with{Epoch=syncEpoch},CancellationToken.None);}
 var entity=Guid.NewGuid();var create=new Mutation(Guid.NewGuid(),"PlanItem",entity,0,new(),new(){["Title"]="Synthetic",["Done"]=false});await Apply(create);await Apply(create);
 long revision;await using(var db=new LifeDb(options)){var item=await db.Items.SingleAsync(x=>x.Id==entity);revision=item.Revision;Check(await db.Receipts.CountAsync(x=>x.Id==create.Id)==1,"Retry must have one receipt.");Check(await db.Changes.CountAsync(x=>x.EntityId==entity)==1,"Retry cannot duplicate feed records.");}
 await Apply(new(Guid.NewGuid(),"PlanItem",entity,revision,new(){["Title"]="Synthetic"},new(){["Title"]="Changed"}));
 await Apply(new(Guid.NewGuid(),"PlanItem",entity,revision,new(){["Done"]=false},new(){["Done"]=true}));
 await using(var db=new LifeDb(options)){var item=await db.Items.SingleAsync(x=>x.Id==entity);var body=JsonNode.Parse(item.Body)!;Check(body["Title"]!.GetValue<string>()=="Changed"&&body["Done"]!.GetValue<bool>(),"Independent fields merge.");}
 await Apply(new(Guid.NewGuid(),"PlanItem",entity,revision,new(){["Title"]="Synthetic"},new(){["Title"]="Divergent"}));
 await using(var db=new LifeDb(options)){Check(await db.Conflicts.CountAsync(x=>x.EntityId==entity)==1,"Same-field divergence is preserved.");}
 var independent=Guid.NewGuid();await Apply(new(Guid.NewGuid(),"PlanItem",independent,0,new(),new(){["Title"]="Changed"}));
 await using(var db=new LifeDb(options)){Check(await db.Items.CountAsync(x=>x.Id==entity||x.Id==independent)==2,"Independent creates must not deduplicate by content.");}
 Console.WriteLine("5 PostgreSQL transaction/sync checks passed.");
}

await ClientSyncChecks.Run();
