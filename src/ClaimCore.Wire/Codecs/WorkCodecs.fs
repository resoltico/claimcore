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
        (record Reminder.decode: Fields<Reminder, _>)
        |> field "reminderId" Reminder.id reminderId
        |> field "caseId" Reminder.caseId caseId
        |> field "revision" Reminder.revision positiveRevision
        |> field "action" Reminder.action reminderAction
        |> field "dueDate" Reminder.due calendarDate
        |> field "kind" Reminder.kind (VocabularyCodec.create ReminderKind.vocabulary)
        |> field "state" Reminder.state (VocabularyCodec.create ReminderState.vocabulary)
        |> field "doneOn" Reminder.doneOn (nullable calendarDate)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "Reminder"

    let party =
        (record (fun id revision content offers ->
            {
                CaseParty.PartyId = id
                Revision = revision
                Content = content
                Offers = offers |> List.map TaskOffer.Declared
            })
        : Fields<CaseParty, _>)
        |> field "partyId" _.PartyId partyId
        |> field "revision" _.Revision positiveRevision
        |> field "content" _.Content partyContent
        |> field
            "acts"
            (fun party -> party.Offers |> List.map TaskOffer.id)
            (list 2 (TaskVocabulary.forSubject ActSubject.Party))
        |> build
        |> named "CaseParty"

    let reminderRead =
        (record
            (fun reminder reference claimant responsible recordedAt recordedBy acts active timing ->
                {
                    ReminderReadModel.Reminder = reminder
                    Reference = reference
                    Claimants = claimant
                    Responsible = responsible
                    RecordedAt = recordedAt
                    RecordedBy = recordedBy
                    Offers = acts |> List.map TaskOffer.Declared
                    Active = active
                    Timing = timing
                })
        : Fields<ReminderReadModel, _>)
        |> field "reminder" _.Reminder reminder
        |> field "caseReference" _.Reference caseReference
        |> field "claimants" _.Claimants (list PartyContent.MaximumCurrent partyName)
        |> field "responsible" _.Responsible (nullable ActorCodecs.actorRef)
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field
            "acts"
            _.Acts
            (list
                (TaskVocabulary.count ActSubject.Reminder)
                (TaskVocabulary.forSubject ActSubject.Reminder))
        |> field "active" _.Active boolean
        |> field "timing" _.Timing (nullable (VocabularyCodec.create ReminderDue.vocabulary))
        |> build
        |> named "ReminderReadModel"

    let relatedCaseTarget =
        (record (fun id reference lifecycle status ->
            {
                RelatedCaseTarget.CaseId = id
                Reference = reference
                Lifecycle = lifecycle
                Status = status
            })
        : Fields<RelatedCaseTarget, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "lifecycle" _.Lifecycle (VocabularyCodec.create Lifecycle.vocabulary)
        |> field "status" _.Status (nullable (VocabularyCodec.create CaseStatus.vocabulary))
        |> build
        |> named "RelatedCaseTarget"

    let relatedCase =
        (record (fun id target note canRemove ->
            {
                RelatedCaseRead.Id = id
                Case = target
                Note = note
                CanRemove = canRemove
            })
        : Fields<RelatedCaseRead, _>)
        |> field "linkId" _.Id linkId
        |> field "case" _.Case (nullable relatedCaseTarget)
        |> field "note" _.Note (nullable noteText)
        |> field "canRemove" _.CanRemove boolean
        |> build
        |> named "RelatedCaseRead"

    let relatedCases =
        (record (fun outgoing incoming incomingCount canAdd ->
            {
                RelatedCaseLinks.Outgoing = outgoing
                Incoming = incoming
                IncomingCount = incomingCount
                CanAdd = canAdd
            })
        : Fields<RelatedCaseLinks, _>)
        |> field "outgoing" _.Outgoing (list RelatedCase.MaximumCurrent relatedCase)
        |> field "incoming" _.Incoming (list RelatedCase.MaximumIncomingShown relatedCase)
        |> field "incomingCount" _.IncomingCount (integer 0L JsonValue.MaximumInteger)
        |> field "canAdd" _.CanAdd boolean
        |> build
        |> named "RelatedCaseLinks"

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
