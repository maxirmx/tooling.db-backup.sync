// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class SchedulePlannerTests
{
    [Fact]
    public void StartupAfterDailyTimeRunsCatchUp()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        var decision = SchedulePlanner.Evaluate(
            new SchedulerState(),
            now,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc);

        Assert.Equal(ScheduledAction.RunNow, decision.Action);
        Assert.Equal(new DateOnly(2026, 8, 11), decision.State.ScheduledDate);
        Assert.Equal(ScheduledSlotStatus.Pending, decision.State.ScheduledStatus);
    }

    [Fact]
    public void StartupBeforeDailyTimeWaits()
    {
        var now = new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);

        var decision = SchedulePlanner.Evaluate(
            new SchedulerState(),
            now,
            new TimeOnly(2, 0),
            TimeZoneInfo.Utc);

        Assert.Equal(ScheduledAction.None, decision.Action);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 2, 0, 0, TimeSpan.Zero), decision.NextWakeUtc);
    }

    [Fact]
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
        Assert.Equal(now.AddMinutes(5), state.NextAttemptUtc);
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(5));
        Assert.Equal(now.AddMinutes(20), state.NextAttemptUtc);
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(20));
        Assert.Equal(now.AddMinutes(50), state.NextAttemptUtc);
        state = SchedulePlanner.RecordScheduledFailure(state, now.AddMinutes(50));

        Assert.Equal(ScheduledSlotStatus.Exhausted, state.ScheduledStatus);
        Assert.Null(state.NextAttemptUtc);
        Assert.Equal(4, state.AttemptsCompleted);
    }

    [Fact]
    public void SpringForwardGapRunsAtFirstValidMinute()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        var utc = SchedulePlanner.ResolveScheduledUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void RepeatedTimeUsesFirstOccurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        var utc = SchedulePlanner.ResolveScheduledUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), utc);
    }
}
