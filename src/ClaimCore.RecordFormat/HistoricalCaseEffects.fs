namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// Permanent request meaning for this record format. No current admission or capability policy.
module HistoricalCaseEffects =
    let private invalid = Error "Invalid historical case transition."

    let private changed (fields: CaseFields) progress = Ok { fields with Progress = progress }

    let private choice (before: 'value option) =
        function
        | Change.Keep -> Ok before
        | Change.Replace value when before.IsSome -> Ok(Some value)
        | Change.Clear when before.IsSome -> Ok None
        | _ -> invalid

    let private correct (fields: CaseFields) (correction: ValidCorrection) =
        let facts =
            match correction.Registration with
            | Amendment.Keep -> fields.Registration
            | Amendment.Replace value -> value

        match
            choice fields.Decision correction.Decision, choice fields.Payment correction.Payment
        with
        | Ok decision, Ok payment ->
            let acknowledged =
                match fields.Progress, correction.Decision, correction.Payment with
                | PaymentProgress.Paid _, Change.Clear, value -> value = Change.Clear
                | PaymentProgress.Paid _, Change.Replace _, Change.Keep -> false
                | _ -> true

            match PaymentProgress.fromGroups decision payment with
            | Ok progress when
                acknowledged && (facts <> fields.Registration || progress <> fields.Progress)
                ->
                Ok
                    { fields with
                        Registration = facts
                        Progress = progress
                    }
            | _ -> invalid
        | _ -> invalid

    let private business (fields: CaseFields) act =
        match act, fields.Status, fields.Progress with
        | CaseAct.AmendRegistration facts, CaseStatus.Opened, PaymentProgress.Undecided ->
            Ok { fields with Registration = facts }
        | CaseAct.Correct correction, CaseStatus.Closed, _
        | CaseAct.Correct correction,
          CaseStatus.Opened,
          (PaymentProgress.Decided _ | PaymentProgress.Paid _) -> correct fields correction
        | CaseAct.Decide decision,
          CaseStatus.Opened,
          (PaymentProgress.Undecided | PaymentProgress.Decided _) ->
            changed fields (PaymentProgress.Decided decision)
        | CaseAct.Bare BareAct.WithdrawDecision, CaseStatus.Opened, PaymentProgress.Decided _ ->
            changed fields PaymentProgress.Undecided
        | CaseAct.RecordPayment payment, CaseStatus.Opened, PaymentProgress.Decided decision when
            Money.value (Decision.payable decision) <> 0M
            ->
            changed fields (PaymentProgress.Paid(decision, payment))
        | CaseAct.Bare BareAct.ClearPayment, CaseStatus.Opened, PaymentProgress.Paid(decision, _) ->
            changed fields (PaymentProgress.Decided decision)
        | CaseAct.Bare BareAct.Close, CaseStatus.Opened, _ ->
            Ok
                { fields with
                    Status = CaseStatus.Closed
                }
        | CaseAct.Bare BareAct.Reopen, CaseStatus.Closed, _ ->
            Ok
                { fields with
                    Status = CaseStatus.Opened
                }
        | _ -> invalid

    let private state act =
        function
        | CaseState.Erased -> invalid
        | CaseState.Active fields ->
            match act with
            | CaseAct.Void _ -> Ok(CaseState.Voided fields)
            | CaseAct.Bare BareAct.Erase -> Ok CaseState.Erased
            | _ -> business fields act |> Result.map CaseState.Active
        | CaseState.Voided fields ->
            match act with
            | CaseAct.Reinstate _ -> Ok(CaseState.Active fields)
            | CaseAct.Bare BareAct.Erase -> Ok CaseState.Erased
            | _ -> invalid

    let apply (request: CaseCommand) (previous: CaseSnapshot option) =
        let next =
            match previous, request.Act with
            | None, CaseAct.Open facts when request.ExpectedRevision = Revision.zero ->
                Ok
                    {
                        CaseId = CaseId.ofOperationId request.OperationId
                        Reference = request.Reference
                        Revision = Revision.first
                        State =
                            CaseState.Active
                                {
                                    CaseReference = request.Reference
                                    Registration = facts
                                    Progress = PaymentProgress.Undecided
                                    Status = CaseStatus.Opened
                                }
                    }
            | Some before, _ when
                before.Revision = request.ExpectedRevision
                && Revision.value before.Revision < Revision.Maximum
                && CaseReference.identity before.Reference =
                    CaseReference.identity request.Reference
                ->
                state request.Act before.State
                |> Result.map (fun state ->
                    { before with
                        Revision = Revision.successor before.Revision
                        State = state
                    })
            | _ -> invalid

        next
        |> Result.bind (fun snapshot ->
            Case.ofAccepted snapshot
            |> Result.map (fun _ -> snapshot)
            |> Result.mapError (fun _ -> "Invalid historical case state."))
