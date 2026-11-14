namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Bounded current entity records, independent of fact snapshots and child cardinality.
module WorkCodecs =
    open Codec
    open ResponseScalars

    let workReadModel =
        (record (fun parent revision responsible summary ->
            {
                CaseWorkReadModel.CaseId = parent
                Revision = revision
                Responsible = responsible
                Summary = summary
            })
        : Fields<CaseWorkReadModel, _>)
        |> field "caseId" _.CaseId caseId
        |> field "revision" _.Revision revision
        |> field "responsible" _.Responsible (nullable ActorCodecs.actorRef)
        |> field "handlingSummary" _.Summary (nullable handlingSummary)
        |> build
        |> named "CaseWorkReadModel"

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
        (record
            (fun reminder reference claimant responsible recordedAt recordedBy acts active timing ->
                {
                    ReminderReadModel.Reminder = reminder
                    Reference = reference
                    ClaimantName = claimant
                    Responsible = responsible
                    RecordedAt = recordedAt
                    RecordedBy = recordedBy
                    Acts = acts
                    Active = active
                    Timing = timing
                })
        : Fields<ReminderReadModel, _>)
        |> field "reminder" _.Reminder reminder
        |> field "caseReference" _.Reference caseReference
        |> field (BusinessField.key BusinessField.ClaimantName) _.ClaimantName partyName
        |> field "responsible" _.Responsible (nullable ActorCodecs.actorRef)
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field
            "acts"
            _.Acts
            (list ReminderActKind.all.Length (VocabularyCodec.create ReminderActKind.vocabulary))
        |> field "active" _.Active boolean
        |> field "timing" _.Timing (nullable (VocabularyCodec.create ReminderDue.vocabulary))
        |> build
        |> named "ReminderReadModel"

    let note =
        (record Note.ofAccepted: Fields<Note, _>)
        |> field "noteId" Note.id noteId
        |> field "caseId" Note.caseId caseId
        |> field "text" Note.text (nullable noteText)
        |> build
        |> named "Note"

    let noteRead =
        (record (fun id text at author remove ->
            {
                NoteReadModel.NoteId = id
                Text = text
                RecordedAt = at
                RecordedBy = author
                CanRemove = remove
            })
        : Fields<NoteReadModel, _>)
        |> field "noteId" _.NoteId noteId
        |> field "text" _.Text noteText
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "canRemove" _.CanRemove boolean
        |> build
        |> named "NoteReadModel"

    let notes =
        (record (fun items count add ->
            {
                CaseNotes.Items = items
                Count = count
                CanAdd = add
            })
        : Fields<CaseNotes, _>)
        |> field "items" _.Items (list 20 noteRead)
        |> field "count" _.Count (integer 0L JsonValue.MaximumInteger)
        |> field "canAdd" _.CanAdd boolean
        |> build
        |> named "CaseNotes"
