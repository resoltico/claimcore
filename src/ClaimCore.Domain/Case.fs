namespace ClaimCore.Domain

open ClaimCore.Contract

open System
open ClaimCore.Domain.ResultFlow

[<NoComparison>]
type CaseRecord =
    private
        {
            Reference: string
            Revision: int64
            State: CaseState<Claim>
        }

module private BusinessState =
    let fields reference (claim: Claim) : CaseFields =
        {
            CaseReference = reference
            Status = claim.Status
            Registration =
                {
                    IncidentDate = Validation.dateText claim.Facts.IncidentDate
                    IncidentNotificationDate =
                        Validation.dateText claim.Facts.IncidentNotificationDate
                    IncidentCountry = claim.Facts.IncidentCountry
                    ClaimantName = claim.Facts.ClaimantName
                    InsurerName = claim.Facts.InsurerName
                    ClaimedAmount = Validation.amountText claim.Facts.ClaimedAmount
                    ClaimedCurrency = claim.Facts.ClaimedAmount.Currency
                }
            Decision =
                PaymentProgress.decision claim.Progress
                |> Option.map (fun value ->
                    {
                        PaymentDecisionDate = Validation.dateText value.PaymentDecisionDate
                        PayableAmount = Validation.amountText value.PayableAmount
                        PayableCurrency = value.PayableAmount.Currency
                    })
            Payment =
                PaymentProgress.paymentDate claim.Progress
                |> Option.map (fun value ->
                    {
                        PaymentDate = Validation.dateText value
                    })
        }

    let private retained value =
        value
        |> Result.mapError (function
            | Refusal.InvalidInput(field, violation) ->
                RecordViolation.InvalidScalar(field, violation)
            | _ -> RecordViolation.InvalidProgress)

    let private restoreProgress (fields: CaseFields) =
        result {
            let! decision =
                match fields.Decision with
                | None -> Ok None
                | Some value -> Validation.decision value |> retained |> Result.map Some

            let! paid =
                match fields.Payment with
                | None -> Ok None
                | Some value ->
                    Validation.date BusinessField.PaymentDate value.PaymentDate
                    |> retained
                    |> Result.map Some

            return! PaymentProgress.fromGroups decision paid |> retained
        }

    let restore (accepted: CaseFields) =
        result {
            let! facts = accepted.Registration |> Validation.registration |> retained
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
                    facts.IncidentNotificationDate

            do! Validation.assertedText None facts

            return
                {
                    Facts = facts
                    Progress = PaymentProgress.Undecided
                    Status = CaseStatus.Opened
                }
        }

    let private change act (claim: Claim) =
        match act with
        | ValidatedAct.Open _ -> invalidOp "An existing case cannot be opened."
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
            let decision = PaymentProgress.decision claim.Progress |> Option.get

            Ok
                { claim with
                    Progress = PaymentProgress.Paid(decision, date)
                }
        | ValidatedAct.ClearPayment ->
            let decision = PaymentProgress.decision claim.Progress |> Option.get

            Ok
                { claim with
                    Progress = PaymentProgress.Decided decision
                }

    let transition today act claim =
        result {
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
    /// Identity normalization never rewrites authored payloads or free text.
    let canonical (request: CaseRequest) =
        let registration (value: Registration) =
            { value with
                ClaimedAmount = Validation.canonicalAmount value.ClaimedAmount
            }

        let decision (value: ClaimCore.Contract.Decision) =
            { value with
                PayableAmount = Validation.canonicalAmount value.PayableAmount
            }

        let registrationGroup =
            function
            | RegistrationGroup.Keep -> RegistrationGroup.Keep
            | RegistrationGroup.Replace value -> RegistrationGroup.Replace(registration value)

        let decisionGroup =
            function
            | DecisionGroup.Keep -> DecisionGroup.Keep
            | DecisionGroup.Clear -> DecisionGroup.Clear
            | DecisionGroup.Replace value -> DecisionGroup.Replace(decision value)

        let act =
            match request.Act with
            | CaseAct.Open value -> CaseAct.Open(registration value)
            | CaseAct.AmendRegistration value -> CaseAct.AmendRegistration(registration value)
            | CaseAct.Decide value -> CaseAct.Decide(decision value)
            | CaseAct.Correct value ->
                CaseAct.Correct
                    { value with
                        Registration = registrationGroup value.Registration
                        Decision = decisionGroup value.Decision
                    }
            | act -> act

        let reference =
            if System.String.IsNullOrEmpty request.CaseReference then
                request.CaseReference
            else
                CaseReference.key request.CaseReference

        { request with
            Act = act
            CaseReference = reference
        }

    let validateRequest request = RequestValidation.parse request

    let snapshot (record: CaseRecord) : CaseSnapshot =
        {
            Reference = record.Reference
            Revision = record.Revision
            State = CaseState.map (BusinessState.fields record.Reference) record.State
        }

    let ofAccepted (accepted: CaseSnapshot) =
        result {
            do!
                CaseReference.validate accepted.Reference
                |> Result.mapError (fun _ -> RecordViolation.ReferenceMismatch)

            if accepted.Revision < 1L || accepted.Revision > Revision.Maximum then
                return! Error RecordViolation.RevisionOutOfRange
            else
                let restore (fields: CaseFields) ctor =
                    if fields.CaseReference <> accepted.Reference then
                        Error RecordViolation.ReferenceMismatch
                    else
                        BusinessState.restore fields |> Result.map ctor

                let! state =
                    match accepted.State with
                    | CaseState.Erased -> Ok CaseState.Erased
                    | CaseState.Active fields -> restore fields CaseState.Active
                    | CaseState.Voided fields -> restore fields CaseState.Voided

                return
                    {
                        Reference = accepted.Reference
                        Revision = accepted.Revision
                        State = state
                    }
        }

    let private steward act claim =
        match act with
        | CaseAct.Void reason ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Voided claim)
        | CaseAct.Reinstate reason ->
            Validation.admission InputTarget.Reason reason
            |> Result.map (fun () -> CaseState.Active claim)
        | CaseAct.Erase -> Ok CaseState.Erased
        | _ -> invalidArg (nameof act) "Expected a stewardship act."

    let private transition today act state =
        result {
            do! Transitions.check state (CaseActs.kind act)
            let claim = CaseState.fields state |> Option.get

            match act with
            | CaseAct.Void _
            | CaseAct.Reinstate _
            | CaseAct.Erase -> return! steward act claim
            | _ ->
                let! parsed = RequestValidation.business act
                let! next = BusinessState.transition today parsed claim
                return CaseState.Active next
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
                        State = CaseState.Active claim
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
            | (CaseState.Active _ | CaseState.Voided _) as state, act ->
                transition today act state
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
            CaseActKinds.all |> List.filter (Transitions.check record.State >> Result.isOk)

    let correctionActions (record: CaseRecord) group =
        match record.State with
        | CaseState.Active claim -> CaseCorrections.availableActions claim.Progress group
        | _ -> [ CorrectionGroupAction.Keep ]
