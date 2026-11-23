namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type Tone =
    | Neutral
    | Info
    | Progress
    | Success
    | Warning
    | Danger

module Tones =
    let vocabulary = Vocabulary.ofUnion<Tone> "tone" TokenCasing.ScreamingSnake None

    let private codes codeVocabulary classify =
        Vocabulary.name codeVocabulary,
        (Vocabulary.entries codeVocabulary
         |> List.map (fun (value, token) -> token, Vocabulary.token vocabulary (classify value))
         |> Map.ofList)

    let codeTones =
        [
            codes CaseHeadline.vocabulary (function
                | CaseHeadline.Undecided -> Tone.Info
                | CaseHeadline.Decided
                | CaseHeadline.PartlyPaid -> Tone.Progress
                | CaseHeadline.Paid -> Tone.Success
                | CaseHeadline.Voided -> Tone.Danger
                | CaseHeadline.Closed
                | CaseHeadline.Erased -> Tone.Neutral)
            codes CaseStatus.vocabulary (function
                | CaseStatus.Opened -> Tone.Info
                | CaseStatus.Closed -> Tone.Neutral)
            codes Lifecycle.vocabulary (function
                | Lifecycle.Active -> Tone.Info
                | Lifecycle.Voided -> Tone.Danger
                | Lifecycle.Erased -> Tone.Neutral)
            codes CaseProgress.vocabulary (function
                | CaseProgress.Undecided -> Tone.Info
                | CaseProgress.Decided
                | CaseProgress.PartlyPaid -> Tone.Progress
                | CaseProgress.Paid -> Tone.Success
                | CaseProgress.Overpaid -> Tone.Warning)
            codes CaseAttention.vocabulary (function
                | CaseAttention.DeadlineOverdue
                | CaseAttention.Overdue -> Tone.Danger
                | CaseAttention.DueToday
                | CaseAttention.NoNextAction
                | CaseAttention.Unassigned
                | CaseAttention.AssigneeUnavailable
                | CaseAttention.PaymentsNeedReview -> Tone.Warning
                | CaseAttention.ReadyToClose -> Tone.Success)
            codes ReminderDue.vocabulary (function
                | ReminderDue.Overdue -> Tone.Danger
                | ReminderDue.Today -> Tone.Warning
                | ReminderDue.Upcoming -> Tone.Info)
            codes ReminderState.vocabulary (function
                | ReminderState.Pending -> Tone.Info
                | ReminderState.Completed -> Tone.Success
                | ReminderState.Cancelled -> Tone.Neutral)
            codes DeadlineOutcome.vocabulary (function
                | DeadlineOutcome.Overdue
                | DeadlineOutcome.Missed -> Tone.Danger
                | DeadlineOutcome.Met -> Tone.Success
                | DeadlineOutcome.Cancelled -> Tone.Neutral)
            codes AccountState.vocabulary (function
                | AccountState.Active -> Tone.Success
                | AccountState.NoRoles -> Tone.Warning
                | AccountState.Disabled -> Tone.Neutral)
            codes RequesterKind.vocabulary (function
                | RequesterKind.Active -> Tone.Success
                | RequesterKind.NoRoles -> Tone.Warning
                | RequesterKind.Disabled -> Tone.Neutral
                | RequesterKind.NotRegistered -> Tone.Info)
            codes ClosureOutcome.vocabulary (function
                | ClosureOutcome.Settled -> Tone.Success
                | ClosureOutcome.Declined -> Tone.Danger
                | ClosureOutcome.Withdrawn
                | ClosureOutcome.Duplicate
                | ClosureOutcome.Other -> Tone.Neutral)
        ]
        |> Map.ofList
