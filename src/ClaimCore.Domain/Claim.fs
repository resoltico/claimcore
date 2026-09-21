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
        let decision, paymentDate =
            match claim.Progress with
            | PaymentProgress.Undecided -> None, None
            | PaymentProgress.Decided decision -> Some decision, None
            | PaymentProgress.Paid(decision, paid) -> Some decision, Some(Validation.dateText paid)

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

    let private restorePayment decision paid =
        result {
            match paid with
            | None -> return PaymentProgress.Decided decision
            | Some rawDate ->
                let! date = Validation.date InputTarget.PaymentDate rawDate
                do! Validation.onOrBefore InputTarget.PaymentDate decision.Date date

                if decision.Payable.Value = 0M then
                    return! Error DomainError.ZeroDecisionCannotBePaid
                else
                    return PaymentProgress.Paid(decision, date)
        }

    let private restoreDecision (facts: Validation.Facts) decisionDate amount currency paid =
        result {
            let! decision =
                Validation.decision
                    {
                        PaymentDecisionDate = decisionDate
                        PayableAmount = amount
                        PayableCurrency = currency
                    }

            do!
                Validation.onOrBefore
                    InputTarget.PaymentDecisionDate
                    facts.NotificationDate
                    decision.Date

            return! restorePayment decision paid
        }

    let private restoreProgress facts (fields: CaseFields) =
        match
            fields.PaymentDecisionDate,
            fields.PayableAmount,
            fields.PayableCurrency,
            fields.PaymentDate
        with
        | None, None, None, None -> Ok PaymentProgress.Undecided
        | None, None, None, Some _ -> Error DomainError.DecisionRequired
        | Some decisionDate, Some amount, Some currency, paid ->
            restoreDecision facts decisionDate amount currency paid
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

            let! progress = restoreProgress facts fields

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

    let private openCase today (request: CommandRequest) registration =
        result {
            let! facts = Validation.registration registration

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

    let private changePayment today command claim =
        match command, claim.Progress with
        | Command.Decide rawDecision, _ ->
            result {
                let! decision = Validation.decision rawDecision

                do!
                    Validation.onOrBefore
                        InputTarget.PaymentDecisionDate
                        claim.Facts.NotificationDate
                        decision.Date

                do! Validation.notFuture InputTarget.PaymentDecisionDate today decision.Date

                return
                    { claim with
                        Progress = PaymentProgress.Decided decision
                    }
            }
        | Command.WithdrawDecision, _ ->
            Ok
                { claim with
                    Progress = PaymentProgress.Undecided
                }
        | Command.RecordPayment rawDate, PaymentProgress.Decided decision ->
            result {
                let! paidDate = Validation.date InputTarget.PaymentDate rawDate
                do! Validation.onOrBefore InputTarget.PaymentDate decision.Date paidDate
                do! Validation.notFuture InputTarget.PaymentDate today paidDate

                return
                    { claim with
                        Progress = PaymentProgress.Paid(decision, paidDate)
                    }
            }
        | Command.ClearPayment, PaymentProgress.Paid(decision, _) ->
            Ok
                { claim with
                    Progress = PaymentProgress.Decided decision
                }
        | _ ->
            Validation.invalidCommand InputTarget.Command CommandViolation.StateTransitionMismatch

    let private change today command claim =
        result {
            do! Eligibility.check (Commands.kind command) claim.Status claim.Progress

            match command with
            | Command.Open _ -> return! Error DomainError.AlreadyExists
            | Command.Reopen ->
                return
                    { claim with
                        Status = CaseStatus.Opened
                    }
            | Command.Close ->
                return
                    { claim with
                        Status = CaseStatus.Closed
                    }
            | Command.AmendRegistration rawFacts ->
                let! facts = Validation.registration rawFacts

                do!
                    Validation.notFuture
                        InputTarget.IncidentNotificationDate
                        today
                        facts.NotificationDate

                return { claim with Facts = facts }
            | Command.CorrectCase correction ->
                let! fields = CaseCorrections.apply today (view claim).Fields correction

                let! corrected =
                    restore
                        {
                            Fields = fields
                            Version = claim.Revision
                        }

                return corrected
            | _ -> return! changePayment today command claim
        }

    /// Envelope and payload admission stays behind the public Claim facade.
    let validateRequest request = RequestValidation.validate request

    /// The same authority on transitions is called by every application adapter.
    /// An injectable business date keeps the domain free from environment/clock reads.
    let decide (today: DateOnly) (request: CommandRequest) (current: Claim option) =
        result {
            do! validateRequest request

            match current with
            | None ->
                match request.Command with
                | Command.Open registration when request.ExpectedVersion = 0L ->
                    return! openCase today request registration
                | Command.Open _ -> return! Error(DomainError.VersionConflict 0L)
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
                    let! changed = change today request.Command claim

                    return
                        { changed with
                            Revision = claim.Revision + 1L
                        }
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
