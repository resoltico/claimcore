namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Only domain functions construct accepted case state. A CaseView is not accepted state.
type Claim =
    private
        {
            Reference: string
            Revision: int64
            Facts: Validation.Facts
            Progress: PaymentProgress
            Status: CaseStatus
        }

module Claim =
    /// Canonical validation for both mutation and query references.
    let validateReference reference =
        Validation.text InputTarget.CaseReference reference |> Result.map ignore

    let view claim =
        let decision = PaymentProgress.decision claim.Progress

        let paymentDate =
            PaymentProgress.paymentDate claim.Progress |> Option.map Validation.dateText

        {
            Fields =
                {
                    IncidentDate = Validation.dateText claim.Facts.IncidentDate
                    IncidentNotificationDate = Validation.dateText claim.Facts.NotificationDate
                    IncidentCountry = claim.Facts.Country
                    ClaimantName = claim.Facts.Claimant
                    InsurerName = claim.Facts.Insurer
                    ClaimedAmount = Validation.amountText claim.Facts.Claimed
                    ClaimedCurrency = claim.Facts.Claimed.Currency
                    CaseReference = claim.Reference
                    PaymentDecisionDate =
                        decision |> Option.map (fun value -> Validation.dateText value.Date)
                    PayableAmount =
                        decision |> Option.map (fun value -> Validation.amountText value.Payable)
                    PayableCurrency = decision |> Option.map (fun value -> value.Payable.Currency)
                    PaymentDate = paymentDate
                    Status = claim.Status
                }
            Version = claim.Revision
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
        | None, None, None -> Error DomainError.DecisionRequired
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

    /// Restore the same thirteen fields. An incomplete decision tuple cannot become accepted state.
    /// Historical restoration does not apply today's clock to old data.
    let restore (snapshot: CaseView) =
        result {
            let fields = snapshot.Fields

            let! reference = Validation.text InputTarget.CaseReference fields.CaseReference

            let! facts = fields |> registration |> Validation.registration

            let! progress = restoreProgress fields
            do! StateValidation.progress facts progress

            if snapshot.Version < 1L || snapshot.Version = Int64.MaxValue then
                return!
                    Validation.invalidCommand
                        InputTarget.Version
                        CommandViolation.StoredVersionOutOfRange
            else
                return
                    {
                        Reference = reference
                        Revision = snapshot.Version
                        Facts = facts
                        Progress = progress
                        Status = fields.Status
                    }
        }

    let private openCase today (request: CommandRequest) (facts: Validation.Facts) =
        result {
            do!
                Validation.notFuture
                    InputTarget.IncidentNotificationDate
                    today
                    facts.NotificationDate

            return
                {
                    Reference = request.CaseReference
                    Revision = 1L
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
        | _ ->
            Validation.invalidCommand InputTarget.Command CommandViolation.StateTransitionMismatch

    let private change command (claim: Claim) =
        match command with
        | ValidatedCommand.Open _ -> Error DomainError.AlreadyExists
        | ValidatedCommand.Reopen -> Ok(claim.Facts, claim.Progress, CaseStatus.Opened)
        | ValidatedCommand.Close -> Ok(claim.Facts, claim.Progress, CaseStatus.Closed)
        | ValidatedCommand.AmendRegistration facts -> Ok(facts, claim.Progress, claim.Status)
        | ValidatedCommand.CorrectCase correction ->
            CaseCorrections.apply claim.Facts claim.Progress correction
            |> Result.map (fun (facts, progress) -> facts, progress, claim.Status)
        | _ ->
            changePayment command claim.Progress
            |> Result.map (fun progress -> claim.Facts, progress, claim.Status)

    /// A candidate becomes opaque accepted state only after both invariant checks pass.
    let private transition today kind command claim =
        result {
            do! Eligibility.check kind claim.Status claim.Progress
            let! facts, progress, status = change command claim
            do! StateValidation.progress facts progress
            do! StateValidation.newlyAssertedDates today claim.Facts claim.Progress facts progress

            return
                { claim with
                    Facts = facts
                    Progress = progress
                    Status = status
                    Revision = claim.Revision + 1L
                }
        }

    /// Envelope and payload admission stays behind the public Claim facade.
    let validateRequest request =
        RequestValidation.parse request |> Result.map ignore

    /// The same authority on transitions is called by every application adapter.
    /// An injectable business date keeps the domain free from environment/clock reads.
    let decide (today: DateOnly) (request: CommandRequest) (current: Claim option) =
        result {
            let! command = RequestValidation.parse request

            match current with
            | None ->
                match command with
                | ValidatedCommand.Open registration when request.ExpectedVersion = 0L ->
                    return! openCase today request registration
                | ValidatedCommand.Open _ -> return! Error(DomainError.VersionConflict 0L)
                | _ -> return! Error DomainError.NotFound
            | Some claim ->
                if claim.Reference <> request.CaseReference then
                    return!
                        Validation.invalidCommand
                            InputTarget.CaseReference
                            CommandViolation.CaseReferenceMismatch
                elif claim.Revision <> request.ExpectedVersion then
                    return! Error(DomainError.VersionConflict claim.Revision)
                elif claim.Revision = Int64.MaxValue - 1L then
                    return!
                        Validation.invalidCommand
                            InputTarget.Version
                            CommandViolation.RevisionExhausted
                else
                    return! transition today (Commands.kind request.Command) command claim
        }


    /// Advisory state actions only. Execution always rechecks state, payload, clock and revision.
    let availableCommands claim =
        if claim.Revision >= Int64.MaxValue - 1L then
            []
        else
            CommandKinds.all
            |> List.filter (fun kind ->
                Eligibility.check kind claim.Status claim.Progress |> Result.isOk)
            |> List.map CommandKinds.token
