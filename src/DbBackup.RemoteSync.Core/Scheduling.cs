namespace DbBackup.RemoteSync;

public enum ScheduledAction
{
    None,
    RunNow,
}

public sealed record ScheduleDecision(ScheduledAction Action, SchedulerState State, DateTimeOffset NextWakeUtc);

public static class SchedulePlanner
{
    public static ScheduleDecision Evaluate(
        SchedulerState state,
        DateTimeOffset utcNow,
        TimeOnly scheduledLocalTime,
        TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var dueUtc = ResolveScheduledUtc(localDate, scheduledLocalTime, timeZone);

        if (state.ScheduledDate != localDate)
        {
            if (utcNow >= dueUtc)
            {
                state = state with
                {
                    ScheduledDate = localDate,
                    ScheduledStatus = ScheduledSlotStatus.Pending,
                    AttemptsCompleted = 0,
                    NextAttemptUtc = utcNow,
                };
            }
            else
            {
                return new(ScheduledAction.None, state, dueUtc);
            }
        }

        if (state.ScheduledStatus == ScheduledSlotStatus.Pending &&
            state.NextAttemptUtc is { } nextAttempt &&
            nextAttempt <= utcNow)
        {
            return new(ScheduledAction.RunNow, state, utcNow);
        }

        var nextWake = state.ScheduledStatus == ScheduledSlotStatus.Pending && state.NextAttemptUtc is { } retry
            ? retry
            : ResolveScheduledUtc(localDate.AddDays(1), scheduledLocalTime, timeZone);
        return new(ScheduledAction.None, state, nextWake);
    }

    public static SchedulerState RecordScheduledSuccess(SchedulerState state, DateOnly localDate) =>
        state with
        {
            ScheduledDate = localDate,
            ScheduledStatus = ScheduledSlotStatus.Succeeded,
            AttemptsCompleted = 0,
            NextAttemptUtc = null,
        };

    public static SchedulerState RecordScheduledFailure(SchedulerState state, DateTimeOffset utcNow)
    {
        var attempts = state.AttemptsCompleted + 1;
        if (attempts <= ProductConstants.RetryDelays.Count)
        {
            return state with
            {
                ScheduledStatus = ScheduledSlotStatus.Pending,
                AttemptsCompleted = attempts,
                NextAttemptUtc = utcNow + ProductConstants.RetryDelays[attempts - 1],
            };
        }

        return state with
        {
            ScheduledStatus = ScheduledSlotStatus.Exhausted,
            AttemptsCompleted = attempts,
            NextAttemptUtc = null,
        };
    }

    public static DateTimeOffset ResolveScheduledUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, offsets.Max()).ToUniversalTime();
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }
}
