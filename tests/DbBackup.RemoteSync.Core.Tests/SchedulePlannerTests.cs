// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class SchedulePlannerTests
{
    [Test]
    public void StartupAfterDailyTimeRunsCatchUp()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        var decision = SchedulePlanner.Evaluate(
            new SchedulerState(),
            now,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc);

        Assert.That(decision.Action, Is.EqualTo(ScheduledAction.RunNow));
        Assert.That(decision.State.ScheduledDate, Is.EqualTo(new DateOnly(2026, 8, 11)));
        Assert.That(decision.State.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Pending));
    }

    [Test]
    public void StartupAfterDailyTimeSkipsCatchUpWhenRequested()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        var decision = SchedulePlanner.Evaluate(
            new SchedulerState(),
            now,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc,
            skipDueAtStartup: true);

        Assert.That(decision.Action, Is.EqualTo(ScheduledAction.None));
        Assert.That(decision.State.ScheduledDate, Is.EqualTo(new DateOnly(2026, 8, 11)));
        Assert.That(decision.State.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Skipped));
        Assert.That(decision.NextWakeUtc, Is.EqualTo(new DateTimeOffset(2026, 8, 12, 2, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void StartupBeforeDailyTimeStillRunsWhenScheduleBecomesDue()
    {
        var state = new SchedulerState();
        var startup = SchedulePlanner.Evaluate(
            state,
            new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero),
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc,
            skipDueAtStartup: true);

        var due = SchedulePlanner.Evaluate(
            startup.State,
            startup.NextWakeUtc,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc);

        Assert.That(due.Action, Is.EqualTo(ScheduledAction.RunNow));
    }

    [Test]
    public void StartupBeforeDailyTimeWaits()
    {
        var now = new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);

        var decision = SchedulePlanner.Evaluate(
            new SchedulerState(),
            now,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc);

        Assert.That(decision.Action, Is.EqualTo(ScheduledAction.None));
        Assert.That(decision.NextWakeUtc, Is.EqualTo(new DateTimeOffset(2026, 8, 11, 2, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void InitialFailureAndThreeRetriesEndExhausted()
    {
        var now = new DateTimeOffset(2026, 8, 11, 2, 0, 0, TimeSpan.Zero);
        var state = new SchedulerState
        {
            ScheduledDate = new DateOnly(2026, 8, 11),
            ScheduledStatus = ScheduledSlotStatus.Pending,
            NextAttemptUtc = now,
        };

        state = SchedulePlanner.RecordScheduledFailure(state, now);
        Assert.That(state.NextAttemptUtc, Is.EqualTo(now.AddMinutes(5)));
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(5));
        Assert.That(state.NextAttemptUtc, Is.EqualTo(now.AddMinutes(20)));
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(20));
        Assert.That(state.NextAttemptUtc, Is.EqualTo(now.AddMinutes(50)));
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(50));

        Assert.That(state.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Exhausted));
        Assert.That(state.NextAttemptUtc, Is.Null);
        Assert.That(state.AttemptsCompleted, Is.EqualTo(4));
    }

    [Test]
    public void SpringForwardGapRunsAtFirstValidMinute()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        var utc = SchedulePlanner.ResolveScheduledUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), zone);

        Assert.That(utc, Is.EqualTo(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void RepeatedTimeUsesFirstOccurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        var utc = SchedulePlanner.ResolveScheduledUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone);

        Assert.That(utc, Is.EqualTo(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)));
    }

    [Test]
    public void ManualSuccessBeforeDailyTimeDoesNotSatisfySlot()
    {
        var state = new SchedulerState();

        var updated = SchedulePlanner.RecordSuccessfulRun(
            state,
            new DateTimeOffset(2026, 8, 11, 1, 59, 0, TimeSpan.Zero),
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc,
            isScheduled: false);

        Assert.That(updated, Is.EqualTo(state));
    }

    [Test]
    public void ManualSuccessAfterDailyTimeSatisfiesStartDateSlot()
    {
        var updated = SchedulePlanner.RecordSuccessfulRun(
            new SchedulerState(),
            new DateTimeOffset(2026, 8, 11, 2, 1, 0, TimeSpan.Zero),
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc,
            isScheduled: false);

        Assert.That(updated.ScheduledDate, Is.EqualTo(new DateOnly(2026, 8, 11)));
        Assert.That(updated.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Succeeded));
    }

    [Test]
    public void ScheduledRetryAfterMidnightSatisfiesOriginalSlot()
    {
        var state = new SchedulerState
        {
            ScheduledDate = new DateOnly(2026, 8, 11),
            ScheduledStatus = ScheduledSlotStatus.Pending,
            AttemptsCompleted = 2,
        };

        var updated = SchedulePlanner.RecordSuccessfulRun(
            state,
            new DateTimeOffset(2026, 8, 12, 0, 5, 0, TimeSpan.Zero),
            new TimeOnly(23, 55),
            TimeZoneInfo.Utc,
            isScheduled: true);

        Assert.That(updated.ScheduledDate, Is.EqualTo(new DateOnly(2026, 8, 11)));
        Assert.That(updated.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Succeeded));
        Assert.That(updated.AttemptsCompleted, Is.EqualTo(0));
    }

    [Test]
    public void CanceledRunAfterDailyTimeSkipsSlot()
    {
        var updated = SchedulePlanner.RecordCanceledRun(
            new SchedulerState(),
            new DateTimeOffset(2026, 8, 11, 2, 1, 0, TimeSpan.Zero),
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc,
            isScheduled: false);

        Assert.That(updated.ScheduledDate, Is.EqualTo(new DateOnly(2026, 8, 11)));
        Assert.That(updated.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Skipped));
        Assert.That(updated.NextAttemptUtc, Is.Null);
    }
}
