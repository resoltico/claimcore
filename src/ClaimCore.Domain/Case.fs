namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

[<NoComparison>]
type Claim =
    private
        {
            Facts: Validation.Facts
            Progress: PaymentProgress
            Status: CaseStatus
        }

[<RequireQualifiedAccess; NoComparison>]
type CaseState =
    | Live of claim: Claim * disposition: Disposition
    | Erased

[<NoComparison>]
type CaseRecord =
    private
        {
            Reference: string
            Revision: int64
            State: CaseState
        }

module private BusinessState =
    let fields reference claim =
        let decision = PaymentProgress.decision claim.Progress

        {
            IncidentDate = Validation.dateText claim.Facts.IncidentDate
            IncidentNotificationDate = Validation.dateText claim.Facts.NotificationDate
            IncidentCountry = claim.Facts.Country
            ClaimantName = claim.Facts.Claimant
            InsurerName = claim.Facts.Insurer
            ClaimedAmount = Validation.amountText claim.Facts.Claimed
            ClaimedCurrency = claim.Facts.Claimed.Currency
            CaseReference = reference
            PaymentDecisionDate =
                decision |> Option.map (fun value -> Validation.dateText value.Date)
            PayableAmount =
                decision |> Option.map (fun value -> Validation.amountText value.Payable)
            PayableCurrency = decision |> Option.map (fun value -> value.Payable.Currency)
            PaymentDate =
                PaymentProgress.paymentDate claim.Progress |> Option.map Validation.dateText
            Status = claim.Status
        }

    let private registration (fields: CaseFields) =
        {
            IncidentDate = fields.IncidentDate
            IncidentNotificationDate = fields.IncidentNotificationDate
            IncidentCountry = fields.IncidentCountry
            ClaimantName = fields.ClaimantName
            InsurerName = fields.InsurerName
            ClaimedAmount = fields.ClaimedAmount
            ClaimedCurrency = fields.ClaimedCurrency
        }

    let private restoreProgress (fields: CaseFields) =
        match fields.PaymentDecisionDate, fields.PayableAmount, fields.PayableCurrency with
        | None, None, None when fields.PaymentDate.IsNone -> Ok PaymentProgress.Undecided
        | None, None, None -> Error CaseRefusal.DecisionRequired
        | Some date, Some amount, Some currency ->
            result {
                let! decision =
                    Validation.decision
                        {
                            PaymentDecisionDate = date
                            PayableAmount = amount
                            PayableCurrency = currency
                        }

                let! paid =
                    match fields.PaymentDate with
                    | None -> Ok None
                    | Some value -> Validation.date InputTarget.PaymentDate value |> Result.map Some

                return! PaymentProgress.fromGroups (Some decision) paid
            }
        | _ ->
            Validation.invalidCorrection
                InputTarget.PaymentDecisionDate
                CorrectionViolation.CompleteDecisionRequired

    let restore (accepted: CaseFields) =
        result {
            let! facts = accepted |> registration |> Validation.registration
            let! progress = restoreProgress accepted
            do! StateValidation.progress facts progress

            let claim =
                {
                    Facts = facts
                    Progress = progress
                    Status = accepted.Status
                }

            if fields accepted.CaseReference claim <> accepted then
                return!
                    Validation.invalidAct InputTarget.Fields ActViolation.StateTransitionMismatch
            else
                return claim
        }

    let openCase (today: DateOnly) (facts: Validation.Facts) =
        result {
            do! Validation.notFuture InputTarget.IncidentDate today facts.IncidentDate

            do!
                Validation.notFuture
                    InputTarget.IncidentNotificationDate
                    today
                    facts.NotificationDate

            do! Validation.assertedText None facts

            return
                {
                    Facts = facts
                    Progress = PaymentProgress.Undecided
                    Status = CaseStatus.Opened
                }
        }

    let private changePayment command progress =
        match command, progress with
        | ValidatedCommand.Decide decision, _ -> Ok(PaymentProgress.Decided decision)
        | ValidatedCommand.WithdrawDecision, _ -> Ok PaymentProgress.Undecided
        | ValidatedCommand.RecordPayment date, PaymentProgress.Decided decision ->
            Ok(PaymentProgress.Paid(decision, date))
        | ValidatedCommand.ClearPayment, PaymentProgress.Paid(decision, _) ->
            Ok(PaymentProgress.Decided decision)
        | _ -> Validation.invalidAct InputTarget.Act ActViolation.StateTransitionMismatch

    let private change command (claim: Claim) =
        match command with
        | ValidatedCommand.Open _ -> Error CaseRefusal.AlreadyExists
        | ValidatedCommand.Reopen ->
            Ok
                { claim with
                    Status = CaseStatus.Opened
                }
        | ValidatedCommand.Close ->
            Ok
                { claim with
                    Status = CaseStatus.Closed
                }
        | ValidatedCommand.AmendRegistration facts -> Ok { claim with Facts = facts }
        | ValidatedCommand.CorrectCase correction ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
        | _ ->
            changePayment command claim.Progress
            |> Result.map (fun progress -> { claim with Progress = progress })

    let transition today kind command claim =
        result {
            do! Eligibility.check kind claim.Status claim.Progress
            let! next = change command claim
            do! StateValidation.progress next.Facts next.Progress

            do!
                StateValidation.newlyAssertedDates
                    today
                    claim.Facts
                    claim.Progress
                    next.Facts
                    next.Progress

            do! Validation.assertedText (Some claim.Facts) next.Facts
            return next
        }

module Case =
    let validateRequest request = RequestValidation.parse request

    let snapshot (record: CaseRecord) =
        {
            Reference = record.Reference
            Revision = record.Revision
            View =
                match record.State with
                | CaseState.Erased -> CaseView.Erased
                | CaseState.Live(claim, disposition) ->
                    CaseView.Live(BusinessState.fields record.Reference claim, disposition)
        }

    let ofAccepted (accepted: CaseSnapshot) =
        result {
            do! CaseReference.validate accepted.Reference

            if accepted.Revision < 1L || accepted.Revision > Revision.Maximum then
                return!
                    Validation.invalidAct InputTarget.Revision ActViolation.StoredRevisionOutOfRange
            else
                let! state =
                    match accepted.View with
                    | CaseView.Erased -> Ok CaseState.Erased
                    | CaseView.Live(fields, disposition) when
                        fields.CaseReference = accepted.Reference
                        ->
                        BusinessState.restore fields
                        |> Result.map (fun claim -> CaseState.Live(claim, disposition))
                    | _ ->
                        Validation.invalidAct
                            InputTarget.CaseReference
                            ActViolation.CaseReferenceMismatch

                return
                    {
                        Reference = accepted.Reference
                        Revision = accepted.Revision
                        State = state
                    }
        }

    let private steward act claim disposition =
        match act, disposition with
        | StewardshipAct.Void _, Disposition.Voided -> Error CaseRefusal.AlreadyVoided
        | StewardshipAct.Reinstate _, Disposition.Active -> Error CaseRefusal.NotVoided
        | StewardshipAct.Void reason, Disposition.Active ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Live(claim, Disposition.Voided))
        | StewardshipAct.Reinstate reason, Disposition.Voided ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Live(claim, Disposition.Active))
        | StewardshipAct.Erase, _ -> Ok CaseState.Erased

    let private transition today act claim disposition =
        match act with
        | CaseAct.Stewardship act -> steward act claim disposition
        | CaseAct.Business _ when disposition = Disposition.Voided -> Error CaseRefusal.CaseVoided
        | CaseAct.Business command ->
            result {
                let! parsed = RequestValidation.business command

                let! next =
                    BusinessState.transition today (BusinessCommands.kind command) parsed claim

                return CaseState.Live(next, disposition)
            }

    let private create today (request: CaseRequest) =
        match request.Act with
        | CaseAct.Business(BusinessCommand.Open _) when request.ExpectedRevision <> 0L ->
            Error(CaseRefusal.RevisionConflict 0L)
        | CaseAct.Business(BusinessCommand.Open values) ->
            result {
                let! facts = Validation.registration values
                let! claim = BusinessState.openCase today facts

                return
                    {
                        Reference = request.CaseReference
                        Revision = 1L
                        State = CaseState.Live(claim, Disposition.Active)
                    }
            }
        | _ -> Error CaseRefusal.NotFound

    let private existing today (request: CaseRequest) (record: CaseRecord) =
        if CaseReference.key record.Reference <> CaseReference.key request.CaseReference then
            Validation.invalidAct InputTarget.CaseReference ActViolation.CaseReferenceMismatch
        else
            match record.State, request.Act with
            | CaseState.Erased, _ -> Error CaseRefusal.CaseErased
            | _, CaseAct.Business(BusinessCommand.Open _) -> Error CaseRefusal.AlreadyExists
            | _ when record.Revision <> request.ExpectedRevision ->
                Error(CaseRefusal.RevisionConflict record.Revision)
            | _ when record.Revision = Revision.Maximum -> Error CaseRefusal.RevisionExhausted
            | CaseState.Live(claim, disposition), act ->
                transition today act claim disposition
                |> Result.map (fun state ->
                    { record with
                        Revision = record.Revision + 1L
                        State = state
                    })

    let decide today request current =
        result {
            do! validateRequest request

            do!
                match request.Act with
                | CaseAct.Business(BusinessCommand.CorrectCase correction) ->
                    CaseCorrections.requireChange correction
                | _ -> Ok()

            match current with
            | None -> return! create today request
            | Some record -> return! existing today request record
        }

    let eligibleActs (record: CaseRecord) =
        if record.Revision = Revision.Maximum then
            []
        else
            match record.State with
            | CaseState.Erased -> []
            | CaseState.Live(_, Disposition.Voided) -> [ CaseActKind.Reinstate; CaseActKind.Erase ]
            | CaseState.Live(claim, Disposition.Active) ->
                let business =
                    CaseActKinds.business
                    |> List.filter (fun kind ->
                        Eligibility.check kind claim.Status claim.Progress |> Result.isOk)

                business @ [ CaseActKind.Void; CaseActKind.Erase ]
