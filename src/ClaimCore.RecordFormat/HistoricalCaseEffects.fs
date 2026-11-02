namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// Permanent request meaning for this record format. No current admission or capability policy.
module HistoricalCaseEffects =
    /// Fixed format-3 meaning: only an enabled human CASE_HANDLER can manage case work.
    let workEligible caseId (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && (actor.Grants
            |> List.exists (fun grant ->
                grant.Role = Role.CaseHandler
                && (grant.Scope = Scope.Installation || grant.Scope = Scope.Case caseId)))

    let private invalid = Error "Invalid historical case transition."

    let private changed (fields: CaseFields) progress = Ok { fields with Progress = progress }

    // Permanent field effects are explicit; current merge/admission policy is not invoked.
    let private registration (before: Registration) (update: ValidRegistrationUpdate) =
        let money = defaultArg update.Claimed (Registration.claimed before)

        Registration.create
            [
                BusinessField.IncidentDate,
                Admitted.Date(defaultArg update.IncidentDate (Registration.incidentDate before))
                BusinessField.IncidentNotificationDate,
                Admitted.Date(
                    defaultArg
                        update.NotificationDate
                        (Registration.incidentNotificationDate before)
                )
                BusinessField.IncidentCountry,
                Admitted.Country(defaultArg update.Country (Registration.country before))
                BusinessField.ClaimantName,
                Admitted.Name(defaultArg update.Claimant (Registration.claimant before))
                BusinessField.InsurerName,
                Admitted.Name(defaultArg update.Insurer (Registration.insurer before))
                BusinessField.ClaimedAmount, Admitted.Amount(Money.amount money)
                BusinessField.ClaimedCurrency, Admitted.Currency(Money.currencyValue money)
            ]

    let private decision (before: Decision) (update: ValidDecisionUpdate) =
        let money = defaultArg update.Payable (Decision.payable before)

        Decision.create
            [
                BusinessField.PaymentDecisionDate,
                Admitted.Date(defaultArg update.Date (Decision.date before))
                BusinessField.PayableAmount, Admitted.Amount(Money.amount money)
                BusinessField.PayableCurrency, Admitted.Currency(Money.currencyValue money)
            ]
        |> Result.map Some

    let private decisionChoice (before: Decision option) =
        function
        | Change.Keep -> Ok before
        | Change.Update update when before.IsSome -> decision before.Value update
        | Change.Clear when before.IsSome -> Ok None
        | _ -> invalid

    let private paymentChoice (before: Payment option) =
        function
        | PaymentChange.Keep -> Ok before
        | PaymentChange.Reaffirm when before.IsSome -> Ok before
        | PaymentChange.Replace value when before.IsSome -> Ok(Some value)
        | PaymentChange.Clear when before.IsSome -> Ok None
        | _ -> invalid

    let private work (before: CaseWork) (update: ValidCaseWorkUpdate) =
        let choose current =
            function
            | WorkValue.Keep -> current
            | WorkValue.Clear -> None
            | WorkValue.Set value -> Some value

        {
            CaseWork.Responsible = choose before.Responsible update.Responsible
            FollowUp = choose before.FollowUp update.FollowUp
        }

    let private correct (fields: CaseFields) (correction: ValidCorrection) =
        let facts =
            match correction.Registration with
            | Amendment.Keep -> Ok fields.Registration
            | Amendment.Update update -> registration fields.Registration update

        match
            facts,
            decisionChoice fields.Decision correction.Decision,
            paymentChoice fields.Payment correction.Payment
        with
        | Ok facts, Ok decision, Ok payment ->
            let acknowledged =
                match fields.Progress, correction.Decision, correction.Payment with
                | PaymentProgress.Paid _, Change.Clear, value -> value = PaymentChange.Clear
                | PaymentProgress.Paid _, Change.Update _, PaymentChange.Keep -> false
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
        | CaseAct.AmendRegistration update, CaseStatus.Opened, PaymentProgress.Undecided ->
            registration fields.Registration update
            |> Result.map (fun facts -> { fields with Registration = facts })
        | CaseAct.UpdateWork update, CaseStatus.Opened, _ ->
            let next = work fields.Work update

            if next = fields.Work then
                invalid
            else
                Ok { fields with Work = next }
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
                                    Work = CaseWork.empty
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
