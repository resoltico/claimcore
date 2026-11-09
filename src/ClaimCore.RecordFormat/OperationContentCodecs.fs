namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Contextual private codecs borrow structural identities from the already validated root.
module internal OperationContentCodecs =
    open Codec
    open WorkRecordCodecs

    let private mismatch () =
        invalidOp "A private value does not match its operation kind."

    let private problem path =
        {
            Path = path
            Reason = "Invalid retained operation values."
        }

    let internal caseInput operation (scope: CaseCommandSkeleton) =
        let parse path act =
            Case.parse
                {
                    OperationId = OperationId.value operation
                    CaseReference = CaseReference.format scope.Reference
                    ExpectedRevision = Revision.value scope.ExpectedRevision
                    Act = act
                }
            |> Result.mapError (fun _ -> problem path)

        let payload ctor project codec =
            codec
            |> interpret (fun path value -> parse path (ctor value)) (fun command ->
                project (Case.canonical command).Authored.Act)

        let bare act =
            payload (fun () -> act) (fun _ -> ()) nullValue

        match scope.Act with
        | CaseActKind.Open ->
            payload
                CaseAct.Open
                (function
                | CaseAct.Open value -> value
                | _ -> mismatch ())
                CanonicalInputs.registration
        | CaseActKind.AmendRegistration ->
            payload
                CaseAct.AmendRegistration
                (function
                | CaseAct.AmendRegistration value -> value
                | _ -> mismatch ())
                CanonicalInputs.registrationUpdate
        | CaseActKind.Correct ->
            payload
                CaseAct.Correct
                (function
                | CaseAct.Correct value -> value
                | _ -> mismatch ())
                CanonicalInputs.correction
        | CaseActKind.Decide ->
            payload
                CaseAct.Decide
                (function
                | CaseAct.Decide value -> value
                | _ -> mismatch ())
                CanonicalInputs.decision
        | CaseActKind.RecordPayment ->
            payload
                CaseAct.RecordPayment
                (function
                | CaseAct.RecordPayment value -> value
                | _ -> mismatch ())
                CanonicalInputs.payment
        | CaseActKind.RemovePayment ->
            bare (CaseAct.RemovePayment(OperationId.value scope.SelectedPayment.Value))
        | CaseActKind.WithdrawDecision -> bare (CaseAct.Bare BareAct.WithdrawDecision)
        | CaseActKind.Close -> bare (CaseAct.Bare BareAct.Close)
        | CaseActKind.Reopen -> bare (CaseAct.Bare BareAct.Reopen)
        | CaseActKind.Erase -> bare (CaseAct.Bare BareAct.Erase)
        | CaseActKind.Void ->
            payload
                CaseAct.Void
                (function
                | CaseAct.Void value -> value
                | _ -> mismatch ())
                text
        | CaseActKind.Reinstate ->
            payload
                CaseAct.Reinstate
                (function
                | CaseAct.Reinstate value -> value
                | _ -> mismatch ())
                text

    let private changedPayment reference =
        match reference with
        | None -> nullValue |> map (fun () -> None) (fun _ -> ())
        | Some reference ->
            let identity, construct, project =
                match reference with
                | PaymentReference.Recorded id ->
                    id,
                    PaymentDifference.Recorded,
                    (function
                    | PaymentDifference.Recorded value -> value
                    | _ -> mismatch ())
                | PaymentReference.Removed id ->
                    id,
                    PaymentDifference.Removed,
                    (function
                    | PaymentDifference.Removed value -> value
                    | _ -> mismatch ())

            (record (fun date amount -> PaymentEntry.create identity date amount)
            : Fields<PaymentEntry, _>)
            |> field "paymentDate" PaymentEntry.date date
            |> field "paidAmount" PaymentEntry.amount (scalar Amount.parse Amount.format)
            |> buildResult (fun path _ -> problem path)
            |> map (construct >> Some) (Option.get >> project)

    let case
        (header: OperationHeader)
        (scope: CaseCommandSkeleton)
        (afterRevision: Revision)
        payment
        =
        (record (fun command after payment ->
            {
                CaseOperationValues.Command = command
                Decision =
                    {
                        After =
                            {
                                CaseId = scope.CaseId
                                Reference = scope.Reference
                                Revision = afterRevision
                                State = LiveCase.toState after
                            }
                        Payment = payment
                    }
            })
        : Fields<CaseOperationValues, _>)
        |> field "input" _.Command (caseInput header.OperationId scope)
        |> field
            "after"
            (fun value -> LiveCase.ofState value.Decision.After.State |> Option.get)
            (RecordCodecs.privateLiveState scope.Reference)
        |> field "payment" _.Decision.Payment (changedPayment payment)
        |> build
        |> interpret
            (fun path value ->
                Case.ofAccepted value.Decision.After
                |> Result.map (fun _ -> value)
                |> Result.mapError (fun _ -> problem path))
            id

    let work parent afterRevision =
        (record (fun input after ->
            {
                WorkOperationValues.Update = input
                After =
                    {
                        CaseId = parent
                        Revision = afterRevision
                        Work = after
                    }
            })
        : Fields<WorkOperationValues, _>)
        |> field "input" _.Update update
        |> field "after" (fun value -> value.After.Work) work
        |> build

    let profile =
        CanonicalInputs.actorProfileInput
        |> interpret
            (fun path input ->
                ActorProfile.restore input |> Result.mapError (fun _ -> problem path))
            ActorProfile.authored

    let note parent operation =
        (scalar NoteText.parse NoteText.format)
        |> map
            (fun text ->
                {
                    NoteOperationValues.Command = NoteCommand.AddNote(parent, text)
                    After = Note.ofAccepted (NoteId.ofOperationId operation) parent (Some text)
                })
            (fun value ->
                match value.Command with
                | NoteCommand.AddNote(_, text) -> text
                | _ -> mismatch ())

    let private plan =
        (record (fun action due ->
            {
                ReminderPlan.Action = action
                Due = due
            })
        : Fields<ReminderPlan, _>)
        |> field "action" _.Action action
        |> field "due" _.Due date
        |> build

    let internal reminderInput (scope: ReminderCommandSkeleton) =
        let change act =
            ReminderCommand.Change(
                scope.CaseId,
                scope.ReminderId.Value,
                scope.ExpectedRevision.Value,
                act
            )

        match scope.Act with
        | ReminderActKind.Create ->
            plan
            |> map (fun plan -> ReminderCommand.Create(scope.CaseId, plan)) (function
                | ReminderCommand.Create(_, plan) -> plan
                | _ -> mismatch ())
        | ReminderActKind.Update ->
            plan
            |> map (ReminderChange.Update >> change) (function
                | ReminderCommand.Change(_, _, _, ReminderChange.Update plan) -> plan
                | _ -> mismatch ())
        | ReminderActKind.CompleteAndPlanNext ->
            plan
            |> map (ReminderChange.CompleteAndPlanNext >> change) (function
                | ReminderCommand.Change(_, _, _, ReminderChange.CompleteAndPlanNext plan) -> plan
                | _ -> mismatch ())
        | ReminderActKind.Reopen ->
            date
            |> map (ReminderChange.Reopen >> change) (function
                | ReminderCommand.Change(_, _, _, ReminderChange.Reopen due) -> due
                | _ -> mismatch ())
        | ReminderActKind.Complete ->
            nullValue |> map (fun () -> change ReminderChange.Complete) (fun _ -> ())
        | ReminderActKind.Cancel ->
            nullValue |> map (fun () -> change ReminderChange.Cancel) (fun _ -> ())

    let private reminderValue descriptor =
        let parent, identity, revision, act =
            match descriptor with
            | EffectDescriptor.ReminderCreated(parent, id, revision) ->
                parent, id, revision, ReminderActKind.Create
            | EffectDescriptor.ReminderChanged(parent, id, revision, act) ->
                parent, id, revision, act
            | _ -> mismatch ()

        (record (fun action due state ->
            Reminder.ofAccepted identity parent revision action due state)
        : Fields<Reminder, _>)
        |> field "action" Reminder.action action
        |> field "due" Reminder.due date
        |> field "state" Reminder.state (VocabularyCodec.create ReminderState.vocabulary)
        |> buildResult (fun path _ -> problem path)
        |> interpret
            (fun path reminder ->
                if ReminderState.isResultOf act (Reminder.state reminder) then
                    Ok reminder
                else
                    Error(problem path))
            id

    let reminder (scope: ReminderCommandSkeleton) descriptors =
        let results =
            match descriptors with
            | [ EffectDescriptor.ReminderCreated _ as created ] ->
                reminderValue created
                |> map ReminderEffects.Created (function
                    | ReminderEffects.Created value -> value
                    | _ -> mismatch ())
            | [ EffectDescriptor.ReminderChanged _ as changed ] ->
                reminderValue changed
                |> map ReminderEffects.Changed (function
                    | ReminderEffects.Changed value -> value
                    | _ -> mismatch ())
            | [ changed; created ] ->
                record (fun completed next -> ReminderEffects.CompletedAndPlanned(completed, next))
                |> field
                    "completed"
                    (function
                    | ReminderEffects.CompletedAndPlanned(value, _) -> value
                    | _ -> mismatch ())
                    (reminderValue changed)
                |> field
                    "next"
                    (function
                    | ReminderEffects.CompletedAndPlanned(_, value) -> value
                    | _ -> mismatch ())
                    (reminderValue created)
                |> build
            | _ -> mismatch ()

        (record (fun command effects ->
            {
                ReminderOperationValues.Command = command
                Effects = effects
            })
        : Fields<ReminderOperationValues, _>)
        |> field "input" _.Command (reminderInput scope)
        |> field "after" _.Effects results
        |> build
