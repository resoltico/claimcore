namespace ClaimCore.Wire

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Json


module ReceiptCodecs =
    open Codec
    open ResponseScalars

    let disclosure =
        tagged
            "SnapshotDisclosure"
            [
                variant
                    "DISCLOSED"
                    SnapshotDisclosure.Disclosed
                    (function
                    | SnapshotDisclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "view" id ResponseCodecs.liveState)
                unitVariant "WITHHELD" SnapshotDisclosure.Withheld
                unitVariant "ERASED" SnapshotDisclosure.Erased
            ]

    let private caseFields =
        (record (fun id reference revision act snapshot ->
            {
                CaseReceipt.CaseId = id
                Reference = reference
                Revision = revision
                Act = act
                Snapshot = snapshot
            })
        : Fields<CaseReceipt, _>)
        |> field "caseId" _.CaseId caseId
        |> field (BusinessField.key BusinessField.CaseReference) _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "act" _.Act (VocabularyCodec.create CaseActKind.vocabulary)
        |> field "snapshot" _.Snapshot disclosure

    let private authorityEffect =
        (record (fun act position ->
            {
                AuthorityReceiptEffect.Act = act
                Position = position
            })
        : Fields<AuthorityReceiptEffect, _>)
        |> field "act" _.Act (VocabularyCodec.create AuthorityKind.vocabulary)
        |> field "position" _.Position LedgerCodecs.position
        |> build
        |> named "AuthorityReceiptEffect"

    let private authorityTarget =
        tagged
            "AuthorityTargetDisclosure"
            [
                variant
                    "DISCLOSED"
                    AuthorityTargetDisclosure.Disclosed
                    (function
                    | AuthorityTargetDisclosure.Disclosed actor -> Some actor
                    | _ -> None)
                    (record id |> field "actor" id ActorCodecs.actorRef)
                variant
                    "WITHHELD"
                    AuthorityTargetDisclosure.Withheld
                    (function
                    | AuthorityTargetDisclosure.Withheld actor -> Some actor
                    | _ -> None)
                    (record id |> field "actorId" id actorId)
            ]

    let private authorityFields =
        (record (fun target act effects ->
            {
                AuthorityReceipt.TargetActor = target
                Act = act
                Effects = effects
            })
        : Fields<AuthorityReceipt, _>)
        |> field "targetActor" _.TargetActor authorityTarget
        |> field "act" _.Act (VocabularyCodec.create AuthorityKind.vocabulary)

        |> field "effects" _.Effects (list AuthorityReceipt.MaximumEffects authorityEffect)

    let private caseVariant =
        variant
            (ActSubject.token ActSubject.Case)
            ReceiptSubject.Case
            (function
            | ReceiptSubject.Case value -> Some value
            | _ -> None)
            caseFields

    let private workDisclosure =
        tagged
            "WorkDisclosure"
            [
                variant
                    "DISCLOSED"
                    WorkDisclosure.Disclosed
                    (function
                    | WorkDisclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "view" id WorkCodecs.workReadModel)
                unitVariant "WITHHELD" WorkDisclosure.Withheld
                unitVariant "ERASED" WorkDisclosure.Erased
            ]

    let private reminderDisclosure =
        tagged
            "ReminderDisclosure"
            [
                variant
                    "DISCLOSED"
                    ReminderDisclosure.Disclosed
                    (function
                    | ReminderDisclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "view" id WorkCodecs.reminder)
                unitVariant "WITHHELD" ReminderDisclosure.Withheld
                unitVariant "ERASED" ReminderDisclosure.Erased
            ]

    let private reminderVersion =
        (record (fun id revision snapshot ->
            {
                ReminderVersion.ReminderId = id
                Revision = revision
                Snapshot = snapshot
            })
        : Fields<ReminderVersion, _>)
        |> field "reminderId" _.ReminderId reminderId
        |> field "revision" _.Revision positiveRevision
        |> field "snapshot" _.Snapshot reminderDisclosure
        |> build
        |> named "ReminderVersion"

    let private reminderEffects =
        tagged
            "ReminderReceiptEffects"
            [
                variant
                    "SINGLE"
                    ReminderReceiptEffects.Single
                    (function
                    | ReminderReceiptEffects.Single value -> Some value
                    | _ -> None)
                    (record id |> field "reminder" id reminderVersion)
                variant
                    "COMPLETED_AND_PLANNED"
                    (fun (completed, next) ->
                        ReminderReceiptEffects.CompletedAndPlanned(completed, next))
                    (function
                    | ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
                        Some(completed, next)
                    | _ -> None)
                    (record (fun completed next -> completed, next)
                     |> field "completed" fst reminderVersion
                     |> field "next" snd reminderVersion)
            ]

    let private workReceiptFields =
        (record (fun parent reference revision act snapshot ->
            {
                WorkReceipt.CaseId = parent
                Reference = reference
                Revision = revision
                Act = act
                Snapshot = snapshot
            })
        : Fields<WorkReceipt, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "act" _.Act (VocabularyCodec.create WorkActKind.vocabulary)
        |> field "snapshot" _.Snapshot workDisclosure

    let private noteDisclosure =
        tagged
            "NoteDisclosure"
            [
                variant
                    "DISCLOSED"
                    NoteDisclosure.Disclosed
                    (function
                    | NoteDisclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "view" id WorkCodecs.note)
                unitVariant "WITHHELD" NoteDisclosure.Withheld
                unitVariant "ERASED" NoteDisclosure.Erased
            ]

    let private noteReceiptFields =
        (record (fun parent reference id act snapshot ->
            {
                NoteReceipt.CaseId = parent
                Reference = reference
                NoteId = id
                Act = act
                Snapshot = snapshot
            })
        : Fields<NoteReceipt, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "noteId" _.NoteId noteId
        |> field "act" _.Act (VocabularyCodec.create NoteActKind.vocabulary)
        |> field "snapshot" _.Snapshot noteDisclosure

    let private reminderReceiptFields =
        (record (fun parent reference act effects ->
            {
                ReminderReceipt.CaseId = parent
                Reference = reference
                Act = act
                Effects = effects
            })
        : Fields<ReminderReceipt, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "act" _.Act (VocabularyCodec.create ReminderActKind.vocabulary)
        |> field "effects" _.Effects reminderEffects

    let subject =
        tagged
            "ReceiptSubject"
            [
                caseVariant
                variant
                    "WORK"
                    ReceiptSubject.Work
                    (function
                    | ReceiptSubject.Work value -> Some value
                    | _ -> None)
                    workReceiptFields
                variant
                    "NOTE"
                    ReceiptSubject.Note
                    (function
                    | ReceiptSubject.Note value -> Some value
                    | _ -> None)
                    noteReceiptFields
                variant
                    "REMINDER"
                    ReceiptSubject.Reminder
                    (function
                    | ReceiptSubject.Reminder value -> Some value
                    | _ -> None)
                    reminderReceiptFields
                variant
                    (ActSubject.token ActSubject.Authority)
                    ReceiptSubject.Authority
                    (function
                    | ReceiptSubject.Authority value -> Some value
                    | _ -> None)
                    authorityFields
            ]

    let private header subjectCodec =
        (record (fun id position at by subject basis ->
            {
                OperationId = id
                Position = position
                RecordedAt = at
                RecordedBy = by
                Subject = subject
                Basis = basis
            })
        : Fields<OperationReceipt, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position ResponseCodecs.position
        |> field "recordedAt" _.RecordedAt Codec.instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "subject" _.Subject subjectCodec
        |> field "basis" _.Basis workDateBasis
        |> build

    let private valid (receipt: OperationReceipt) =
        let reminder parent act (version: ReminderVersion) =
            match version.Snapshot with
            | ReminderDisclosure.Disclosed value ->
                Reminder.id value = version.ReminderId
                && Reminder.revision value = version.Revision
                && Reminder.caseId value = parent
                && ReminderState.isResultOf act (Reminder.state value)
            | ReminderDisclosure.Withheld
            | ReminderDisclosure.Erased -> true

        match receipt.Subject with
        | ReceiptSubject.Work value ->
            match value.Snapshot with
            | WorkDisclosure.Disclosed work ->
                work.CaseId = value.CaseId && work.Revision = value.Revision
            | _ -> true
        | ReceiptSubject.Note value ->
            (value.Act <> NoteActKind.AddNote
             || value.NoteId = NoteId.ofOperationId receipt.OperationId)
            && (match value.Snapshot with
                | NoteDisclosure.Disclosed note ->
                    Note.caseId note = value.CaseId
                    && Note.id note = value.NoteId
                    && ((Note.text note).IsSome = (value.Act = NoteActKind.AddNote))
                | NoteDisclosure.Withheld
                | NoteDisclosure.Erased -> true)
        | ReceiptSubject.Reminder value ->
            match value.Act, value.Effects with
            | ReminderActKind.CompleteAndPlanNext,
              ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
                completed.ReminderId <> next.ReminderId
                && next.ReminderId = ReminderId.ofOperationId receipt.OperationId
                && next.Revision = Revision.first
                && reminder value.CaseId ReminderActKind.CompleteAndPlanNext completed
                && reminder value.CaseId ReminderActKind.Create next
            | ReminderActKind.CompleteAndPlanNext, _ -> false
            | _, ReminderReceiptEffects.CompletedAndPlanned _ -> false
            | act, ReminderReceiptEffects.Single version ->
                reminder value.CaseId act version
                && (act <> ReminderActKind.Create
                    || (version.ReminderId = ReminderId.ofOperationId receipt.OperationId
                        && version.Revision = Revision.first))
        | _ -> true

    let receipt =
        header subject
        |> interpret
            (fun path value ->
                if valid value then
                    Ok value
                else
                    Error
                        {
                            Path = path
                            Reason =
                                "Receipt effects disagree with their parent, operation, act or version."
                        })
            id
        |> named "OperationReceipt"
