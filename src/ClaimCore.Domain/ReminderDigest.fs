namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type DigestOutcome =
    | RelayAccepted
    | FailedBeforeAcceptance
    | SkippedEmpty
    | Unknown

module DigestOutcome =
    let vocabulary =
        Vocabulary.ofUnion<DigestOutcome> "digestOutcome" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

/// A relay acknowledgement states acceptance by that relay, independent of final delivery.
[<RequireQualifiedAccess>]
type DigestRelayResult =
    | Accepted
    | Refused
    | Uncertain

type DigestRunSummary =
    {
        InFlight: int64
        Expired: int64
        Terminal: Map<DigestOutcome, int64>
    }

[<RequireQualifiedAccess>]
type DigestCountKind =
    | OverdueDeadlines
    | DeadlinesDueToday
    | UpcomingDeadlines
    | OverdueFollowUps
    | FollowUpsDueToday

module DigestCountKind =
    let vocabulary =
        Vocabulary.ofUnion<DigestCountKind> "digestCount" TokenCasing.ScreamingSnake None

module ReminderDigest =
    let principalKind = PrincipalKind.Human
    let selection = ReminderView.plan ReminderView.Mine
    let attemptDuration = TimeSpan.FromMinutes 1.

    [<Literal>]
    let UpcomingDays = 7

    [<Literal>]
    let RetentionDays = 30

    let dueCutoff (today: DateOnly) =
        DateOnly.FromDayNumber(min DateOnly.MaxValue.DayNumber (today.DayNumber + UpcomingDays))

    let retainFrom (today: DateOnly) =
        DateOnly.FromDayNumber(max DateOnly.MinValue.DayNumber (today.DayNumber - RetentionDays))

    let scheduled (starts: TimeOnly) (local: TimeOnly) = local >= starts

    let counts (today: DateOnly) reminders =
        let category (kind, due: DateOnly) =
            match kind, due.DayNumber - today.DayNumber with
            | ReminderKind.Deadline, days when days < 0 -> Some DigestCountKind.OverdueDeadlines
            | ReminderKind.Deadline, 0 -> Some DigestCountKind.DeadlinesDueToday
            | ReminderKind.Deadline, days when days <= UpcomingDays ->
                Some DigestCountKind.UpcomingDeadlines
            | ReminderKind.FollowUp, days when days < 0 -> Some DigestCountKind.OverdueFollowUps
            | ReminderKind.FollowUp, 0 -> Some DigestCountKind.FollowUpsDueToday
            | _ -> None

        reminders
        |> Seq.choose category
        |> Seq.countBy id
        |> Seq.map (fun (kind, count) -> kind, int64 count)
        |> Map.ofSeq
