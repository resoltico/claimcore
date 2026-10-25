namespace ClaimCore.Domain

open ClaimCore.Contract

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

    let private retained value =
        value
        |> Result.mapError (function
            | Refusal.InvalidInput(field, violation) ->
                RecordViolation.InvalidScalar(field, violation)
            | _ -> RecordViolation.InvalidProgress)

    let private restoreProgress (fields: CaseFields) =
        match fields.PaymentDecisionDate, fields.PayableAmount, fields.PayableCurrency with
        | None, None, None when fields.PaymentDate.IsNone -> Ok PaymentProgress.Undecided
        | None, None, None -> Error RecordViolation.InvalidProgress
        | Some date, Some amount, Some currency ->
            result {
                let! decision =
                    Validation.decision
                        {
                            PaymentDecisionDate = date
                            PayableAmount = amount
                            PayableCurrency = currency
                        }
                    |> retained

                let! paid =
                    match fields.PaymentDate with
                    | None -> Ok None
                    | Some value ->
                        Validation.date (InputTarget.Field BusinessField.PaymentDate) value
                        |> retained
                        |> Result.map Some

                return! PaymentProgress.fromGroups (Some decision) paid |> retained
            }
        | _ -> Error RecordViolation.IncompleteDecision

    let restore (accepted: CaseFields) =
        result {
            let! facts = accepted |> registration |> Validation.registration |> retained
            let! progress = restoreProgress accepted
            do! StateValidation.progress facts progress |> retained

            let claim =
                {
                    Facts = facts
                    Progress = progress
                    Status = accepted.Status
                }

            if fields accepted.CaseReference claim <> accepted then
                return! Error RecordViolation.NonCanonicalFields
            else
                return claim
        }

    let openCase (today: DateOnly) (facts: Validation.Facts) =
        result {
            do!
                Validation.notFuture
                    (InputTarget.Field BusinessField.IncidentDate)
                    today
                    facts.IncidentDate

            do!
                Validation.notFuture
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
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

    let private recordPayment date progress =
        match progress with
        | PaymentProgress.Decided decision -> Ok(PaymentProgress.Paid(decision, date))
        | PaymentProgress.Undecided -> Error Refusal.DecisionRequired
        | PaymentProgress.Paid _ -> Error Refusal.PaymentAlreadyRecorded

    let private clearPayment progress =
        match progress with
        | PaymentProgress.Paid(decision, _) -> Ok(PaymentProgress.Decided decision)
        | PaymentProgress.Undecided
        | PaymentProgress.Decided _ -> Error Refusal.PaymentNotRecorded

    let private change act (claim: Claim) =
        match act with
        | ValidatedAct.Open _ -> Error Refusal.CaseAlreadyExists
        | ValidatedAct.Reopen ->
            Ok
                { claim with
                    Status = CaseStatus.Opened
                }
        | ValidatedAct.Close ->
            Ok
                { claim with
                    Status = CaseStatus.Closed
                }
        | ValidatedAct.AmendRegistration facts -> Ok { claim with Facts = facts }
        | ValidatedAct.Correct correction ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) ->
                { claim with
                    Facts = facts
                    Progress = progress
                })
        | ValidatedAct.Decide decision ->
            Ok
                { claim with
                    Progress = PaymentProgress.Decided decision
                }
        | ValidatedAct.WithdrawDecision ->
            Ok
                { claim with
                    Progress = PaymentProgress.Undecided
                }
        | ValidatedAct.RecordPayment date ->
            recordPayment date claim.Progress
            |> Result.map (fun progress -> { claim with Progress = progress })
        | ValidatedAct.ClearPayment ->
            clearPayment claim.Progress
            |> Result.map (fun progress -> { claim with Progress = progress })

    let transition today kind act claim =
        result {
            do! Eligibility.check kind claim.Status claim.Progress
            let! next = change act claim
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
            do!
                CaseReference.validate accepted.Reference
                |> Result.mapError (fun _ -> RecordViolation.ReferenceMismatch)

            if accepted.Revision < 1L || accepted.Revision > Revision.Maximum then
                return! Error RecordViolation.RevisionOutOfRange
            else
                let! state =
                    match accepted.View with
                    | CaseView.Erased -> Ok CaseState.Erased
                    | CaseView.Live(fields, disposition) when
                        fields.CaseReference = accepted.Reference
                        ->
                        BusinessState.restore fields
                        |> Result.map (fun claim -> CaseState.Live(claim, disposition))
                    | _ -> Error RecordViolation.ReferenceMismatch

                return
                    {
                        Reference = accepted.Reference
                        Revision = accepted.Revision
                        State = state
                    }
        }

    let private steward act claim disposition =
        match act, disposition with
        | CaseAct.Void _, Disposition.Voided -> Error Refusal.AlreadyVoided
        | CaseAct.Reinstate _, Disposition.Active -> Error Refusal.NotVoided
        | CaseAct.Void reason, Disposition.Active ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Live(claim, Disposition.Voided))
        | CaseAct.Reinstate reason, Disposition.Voided ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Live(claim, Disposition.Active))
        | CaseAct.Erase, _ -> Ok CaseState.Erased
        | _ -> invalidArg (nameof act) "Expected a stewardship act."

    let private transition today act claim disposition =
        match act with
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Erase -> steward act claim disposition
        | _ when disposition = Disposition.Voided -> Error Refusal.CaseVoided
        | act ->
            result {
                let! parsed = RequestValidation.business act

                let! next = BusinessState.transition today (ValidatedActs.kind parsed) parsed claim

                return CaseState.Live(next, disposition)
            }

    let private create today (request: CaseRequest) =
        match request.Act with
        | (CaseAct.Open _) when request.ExpectedRevision <> 0L -> Error(Refusal.RevisionConflict 0L)
        | (CaseAct.Open values) ->
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
        | _ -> Error Refusal.ResourceUnavailable

    let private existing today (request: CaseRequest) (record: CaseRecord) =
        if CaseReference.key record.Reference <> CaseReference.key request.CaseReference then
            invalidArg "record" "The authoritative case does not match the request target."
        else
            match record.State, request.Act with
            | CaseState.Erased, _ -> Error Refusal.CaseErased
            | _, (CaseAct.Open _) -> Error Refusal.CaseAlreadyExists
            | _ when record.Revision <> request.ExpectedRevision ->
                Error(Refusal.RevisionConflict record.Revision)
            | _ when record.Revision = Revision.Maximum -> Error Refusal.RevisionExhausted
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
            | CaseState.Live(_, Disposition.Voided) ->
                [
                    (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                    (CaseActKind.Stewardship StewardshipActKind.Erase)
                ]
            | CaseState.Live(claim, Disposition.Active) ->
                let business =
                    CaseActKinds.business
                    |> List.filter (fun kind ->
                        match kind with
                        | CaseActKind.Business business ->
                            Eligibility.check business claim.Status claim.Progress |> Result.isOk
                        | CaseActKind.Stewardship _ -> false)

                business
                @ [
                    (CaseActKind.Stewardship StewardshipActKind.Void)
                    (CaseActKind.Stewardship StewardshipActKind.Erase)
                ]

    let correctionActions (record: CaseRecord) group =
        match record.State with
        | CaseState.Live(claim, Disposition.Active) ->
            CaseCorrections.availableActions claim.Progress group
        | _ -> [ CorrectionGroupAction.Keep ]
