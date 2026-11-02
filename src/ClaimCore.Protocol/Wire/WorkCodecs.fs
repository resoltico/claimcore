namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Bounded current entity records, independent of fact snapshots and child cardinality.
module WorkCodecs =
    open Codec
    open ResponseScalars

    let work =
        (record (fun responsible summary ->
            {
                CaseWork.Responsible = responsible
                Summary = summary
            })
        : Fields<CaseWork, _>)
        |> field "responsibleActor" _.Responsible (nullable actorId)
        |> field "handlingSummary" _.Summary (nullable handlingSummary)
        |> build
        |> named "CaseWork"

    let workSnapshot =
        (record (fun parent revision work ->
            {
                CaseWorkSnapshot.CaseId = parent
                Revision = revision
                Work = work
            })
        : Fields<CaseWorkSnapshot, _>)
        |> field "caseId" _.CaseId caseId
        |> field "revision" _.Revision revision
        |> field "work" _.Work work
        |> build
        |> named "CaseWorkSnapshot"

    let reminder =
        (record (fun id parent revision action due state ->
            Reminder.ofAccepted id parent revision action due state)
        : Fields<Reminder, _>)
        |> field "reminderId" Reminder.id reminderId
        |> field "caseId" Reminder.caseId caseId
        |> field "revision" Reminder.revision positiveRevision
        |> field "action" Reminder.action reminderAction
        |> field "dueDate" Reminder.due calendarDate
        |> field "state" Reminder.state (VocabularyCodec.create ReminderState.vocabulary)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "Reminder"

    let effects =
        tagged
            "ReminderEffects"
            [
                variant
                    "CREATED"
                    ReminderEffects.Created
                    (function
                    | ReminderEffects.Created value -> Some value
                    | _ -> None)
                    (record id |> field "reminder" id reminder)
                variant
                    "CHANGED"
                    ReminderEffects.Changed
                    (function
                    | ReminderEffects.Changed value -> Some value
                    | _ -> None)
                    (record id |> field "reminder" id reminder)
                variant
                    "COMPLETED_AND_PLANNED"
                    (fun (completed, next) -> ReminderEffects.CompletedAndPlanned(completed, next))
                    (function
                    | ReminderEffects.CompletedAndPlanned(completed, next) -> Some(completed, next)
                    | _ -> None)
                    (record (fun completed next -> completed, next)
                     |> field "completed" fst reminder
                     |> field "next" snd reminder)
            ]

    let reminderRead =
        (record (fun reminder reference recordedAt recordedBy acts active ->
            {
                ReminderReadModel.Reminder = reminder
                Reference = reference
                RecordedAt = recordedAt
                RecordedBy = recordedBy
                Acts = acts
                Active = active
            })
        : Fields<ReminderReadModel, _>)
        |> field "reminder" _.Reminder reminder
        |> field "caseReference" _.Reference caseReference
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy actorId
        |> field
            "acts"
            _.Acts
            (list ReminderActKind.all.Length (VocabularyCodec.create ReminderActKind.vocabulary))
        |> field "active" _.Active boolean
        |> build
        |> named "ReminderReadModel"
