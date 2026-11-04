module ClaimCore.Tests.WireWorkGenerators

open System
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Application.Contracts
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.WireScalarGenerators

let private accepted =
    function
    | Ok value -> value
    | Error error -> invalidOp (string error)

let private revision value = Revision.create value |> Option.get
let private id value = ReminderId.create value |> Option.get

let reminder =
    gen {
        let! reminder = guid
        let! parent = guid
        let! version = Gen.int64 (Range.constant 1L SafeInteger.Maximum)
        let! action = scalarText
        let! due = date

        let! state =
            Gen.item [ ReminderState.Pending; ReminderState.Completed; ReminderState.Cancelled ]

        return
            Reminder.ofAccepted
                (id reminder)
                (CaseId.create parent |> Option.get)
                (revision version)
                (ReminderAction.parse action |> accepted)
                (CalendarDate.parse due |> accepted)
                state
            |> accepted
    }

let workSnapshot =
    gen {
        let! parent = guid
        let! empty = Gen.bool
        let! version = Gen.int64 (Range.constant 1L SafeInteger.Maximum)
        let! responsible = Gen.option guid
        let! summary = Gen.option scalarText

        return
            {
                CaseWorkSnapshot.CaseId = CaseId.create parent |> Option.get
                Revision = if empty then Revision.zero else revision version
                Work =
                    if empty then
                        CaseWork.empty
                    else
                        {
                            Responsible = responsible |> Option.map (ActorId.create >> Option.get)
                            Summary = summary |> Option.map (HandlingSummary.parse >> accepted)
                        }
            }
    }

let domainValue (kind: Type) =
    if kind = typeof<ReminderId> then
        Some(guid |> Gen.map (id >> box))
    elif kind = typeof<IncidentDescription> then
        Some(scalarText |> Gen.map (IncidentDescription.parse >> accepted >> box))
    elif kind = typeof<HandlingSummary> then
        Some(scalarText |> Gen.map (HandlingSummary.parse >> accepted >> box))
    elif kind = typeof<ReminderAction> then
        Some(scalarText |> Gen.map (ReminderAction.parse >> accepted >> box))
    elif kind = typeof<Reminder> then
        Some(reminder |> Gen.map box)
    elif kind = typeof<CaseWorkSnapshot> then
        Some(workSnapshot |> Gen.map box)
    else
        None

let private distinctExisting operation candidate =
    if candidate <> ReminderId.ofOperationId operation then
        candidate
    else
        let bytes = OperationId.value operation |> _.ToByteArray()
        bytes[0] <- bytes[0] ^^^ 1uy
        bytes[1] <- bytes[1] ||| 1uy
        Guid bytes |> id

let private reminderVersion parent identity revision state (candidate: ReminderVersion) =
    let snapshot =
        match candidate.Snapshot with
        | ReminderDisclosure.Disclosed value ->
            Reminder.ofAccepted
                identity
                parent
                revision
                (Reminder.action value)
                (Reminder.due value)
                state
            |> accepted
            |> ReminderDisclosure.Disclosed
        | disclosure -> disclosure

    {
        ReminderId = identity
        Revision = revision
        Snapshot = snapshot
    }

/// Input construction preserves independent random payloads but ties the identities/versions
/// required by the receiving contract. Expected effects and corruption controls remain literal fixtures.
let coherentReceipt (receipt: OperationReceipt) =
    let subject =
        match receipt.Subject with
        | ReceiptSubject.Work work ->
            let snapshot =
                match work.Snapshot with
                | WorkDisclosure.Disclosed snapshot ->
                    WorkDisclosure.Disclosed
                        { snapshot with
                            CaseId = work.CaseId
                            Revision = work.Revision
                        }
                | disclosure -> disclosure

            ReceiptSubject.Work { work with Snapshot = snapshot }
        | ReceiptSubject.Reminder reminder ->
            let candidate, next =
                match reminder.Effects with
                | ReminderReceiptEffects.Single value -> value, value
                | ReminderReceiptEffects.CompletedAndPlanned(completed, next) -> completed, next

            let existing = distinctExisting receipt.OperationId candidate.ReminderId
            let version = revision (max 2L (Revision.value candidate.Revision))

            let changed state =
                reminderVersion reminder.CaseId existing version state candidate

            let created value =
                reminderVersion
                    reminder.CaseId
                    (ReminderId.ofOperationId receipt.OperationId)
                    Revision.first
                    ReminderState.Pending
                    value

            let effects =
                match reminder.Act with
                | ReminderActKind.Create -> ReminderReceiptEffects.Single(created candidate)
                | ReminderActKind.Complete ->
                    ReminderReceiptEffects.Single(changed ReminderState.Completed)
                | ReminderActKind.Cancel ->
                    ReminderReceiptEffects.Single(changed ReminderState.Cancelled)
                | ReminderActKind.Update
                | ReminderActKind.Reopen ->
                    ReminderReceiptEffects.Single(changed ReminderState.Pending)
                | ReminderActKind.CompleteAndPlanNext ->
                    ReminderReceiptEffects.CompletedAndPlanned(
                        changed ReminderState.Completed,
                        created next
                    )

            ReceiptSubject.Reminder { reminder with Effects = effects }
        | subject -> subject

    { receipt with Subject = subject }

let private parentReference reference (parent: CaseSnapshot) =
    { parent with
        Reference = reference
        State =
            parent.State
            |> CaseState.map (fun fields ->
                { fields with
                    CaseReference = reference
                })
    }

let private mutableVersion value =
    revision (min (SafeInteger.Maximum - 1L) (max 1L (Revision.value value)))

let private nextVersion value = revision (Revision.value value + 1L)

let private workReview (review: WorkReview) =
    let parent = parentReference review.Reference review.Parent

    let before =
        { review.Before with
            CaseId = parent.CaseId
            Revision =
                revision (min (SafeInteger.Maximum - 1L) (Revision.value review.Before.Revision))
        }

    let summary =
        if
            (review.Proposed.Responsible |> Option.map _.ActorId)
            <> (before.Responsible |> Option.map _.ActorId)
            || review.Proposed.Summary <> before.Summary
        then
            review.Proposed.Summary
        else
            let first = HandlingSummary.parse "Wire handling change" |> accepted

            Some(
                if before.Summary = Some first then
                    HandlingSummary.parse "Another wire handling change" |> accepted
                else
                    first
            )

    { review with
        Parent = parent
        Before = before
        Proposed =
            { review.Proposed with
                CaseId = parent.CaseId
                Revision = nextVersion before.Revision
                Summary = summary
            }
    }

let private reminderReview operation (review: ReminderReview) =
    let parent = parentReference review.Reference review.Parent

    let created value =
        Reminder.ofAccepted
            (ReminderId.ofOperationId operation)
            parent.CaseId
            Revision.first
            (Reminder.action value)
            (Reminder.due value)
            ReminderState.Pending
        |> accepted

    let changed before after =
        let version = mutableVersion (Reminder.revision before)
        let identity = distinctExisting operation (Reminder.id before)

        let before =
            Reminder.ofAccepted
                identity
                parent.CaseId
                version
                (Reminder.action before)
                (Reminder.due before)
                ReminderState.Pending
            |> accepted

        let state = Reminder.state after

        let action, due =
            if state <> ReminderState.Pending then
                Reminder.action before, Reminder.due before
            else
                Reminder.action after, Reminder.due after

        let after =
            Reminder.ofAccepted identity parent.CaseId (nextVersion version) action due state
            |> accepted

        before, after

    match review.Effects with
    | ReminderEffects.Created value ->
        { review with
            Parent = parent
            CaseId = parent.CaseId
            Before = None
            Effects = ReminderEffects.Created(created value)
        }
    | ReminderEffects.Changed value ->
        let before, after = changed (defaultArg review.Before value) value

        { review with
            Parent = parent
            CaseId = parent.CaseId
            Before = Some before
            Effects = ReminderEffects.Changed after
        }
    | ReminderEffects.CompletedAndPlanned(completed, next) ->
        let before, completed = changed (defaultArg review.Before completed) completed

        let completed =
            Reminder.ofAccepted
                (Reminder.id completed)
                parent.CaseId
                (Reminder.revision completed)
                (Reminder.action completed)
                (Reminder.due completed)
                ReminderState.Completed
            |> accepted

        { review with
            Parent = parent
            CaseId = parent.CaseId
            Before = Some before
            Effects = ReminderEffects.CompletedAndPlanned(completed, created next)
        }

let coherentPreview (reply: PresentedPreviewReply) =
    let outcome =
        match reply.Outcome with
        | Outcome.Succeeded(PresentedPreviewed.Reviewed review) ->
            let subject =
                match review.Subject with
                | PresentedReviewSubject.Work value -> PresentedReviewSubject.Work(workReview value)
                | PresentedReviewSubject.Reminder value ->
                    let operation = OperationId.create reply.OperationId |> Option.get
                    PresentedReviewSubject.Reminder(reminderReview operation value)
                | subject -> subject
            Outcome.Succeeded(PresentedPreviewed.Reviewed { review with Subject = subject })
        | outcome -> outcome
    { reply with Outcome = outcome }
