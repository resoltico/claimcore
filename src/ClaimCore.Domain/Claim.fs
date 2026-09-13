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
        Validation.text "caseReference" reference |> Result.map ignore

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
                let! date = Validation.date "paymentDate" rawDate
                do! Validation.onOrBefore "paymentDate" decision.Date date

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

            do! Validation.onOrBefore "paymentDecisionDate" facts.NotificationDate decision.Date

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
            Validation.invalid
                "paymentDecisionDate"
                "Decision date, payable amount and payable currency must be present together or all absent."

    /// Restore the same thirteen fields. An incomplete decision tuple cannot become accepted state.
    /// Historical restoration does not apply today's clock to old data.
    let restore (snapshot: CaseView) =
        result {
            let fields = snapshot.Fields

            let! reference = Validation.text "caseReference" fields.CaseReference

            let! facts = fields |> registration |> Validation.registration

            let! progress = restoreProgress facts fields

            if snapshot.Version < 1L || snapshot.Version = Int64.MaxValue then
                return!
                    Validation.invalid
                        "version"
                        "Stored versions must be positive and below Int64.MaxValue."
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
            do! Validation.notFuture "incidentNotificationDate" today facts.NotificationDate

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
                        "paymentDecisionDate"
                        claim.Facts.NotificationDate
                        decision.Date

                do! Validation.notFuture "paymentDecisionDate" today decision.Date

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
                let! paidDate = Validation.date "paymentDate" rawDate
                do! Validation.onOrBefore "paymentDate" decision.Date paidDate
                do! Validation.notFuture "paymentDate" today paidDate

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
        | _ -> Validation.invalid "command" "The state guard and payment transition disagree."

    /// State-only eligibility. Payload/date/version checks still run for every submitted command.
    let private eligibility kind claim =
        Eligibility.check kind claim.Status claim.Progress

    let private change today command claim =
        result {
            do! eligibility (Commands.kind command) claim

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
                do! Validation.notFuture "incidentNotificationDate" today facts.NotificationDate
                return { claim with Facts = facts }
            | _ -> return! changePayment today command claim
        }

    /// Envelope and structural payload checks run before identity encoding and again at decision time.
    let validateRequest (request: CommandRequest) =
        result {
            do! validateReference request.CaseReference

            if request.OperationId = Guid.Empty then
                return! Validation.invalid "operationId" "Use a non-empty UUID."
            elif request.ExpectedVersion < 0L || request.ExpectedVersion = Int64.MaxValue then
                return!
                    Validation.invalid
                        "expectedVersion"
                        "Use a non-negative version below Int64.MaxValue."
            else
                match request.Command with
                | Command.Open input
                | Command.AmendRegistration input ->
                    return! Validation.registration input |> Result.map ignore
                | Command.Decide input -> return! Validation.decision input |> Result.map ignore
                | Command.RecordPayment value ->
                    return! Validation.date "paymentDate" value |> Result.map ignore
                | Command.WithdrawDecision
                | Command.ClearPayment
                | Command.Close
                | Command.Reopen -> return ()
        }

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
                        Validation.invalid
                            "caseReference"
                            "The loaded case does not match this command."
                elif claim.Revision <> request.ExpectedVersion then
                    return! Error(DomainError.VersionConflict claim.Revision)
                elif claim.Revision = Int64.MaxValue - 1L then
                    return!
                        Validation.invalid
                            "version"
                            "The case revision cannot advance below Int64.MaxValue."
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
            |> List.filter (fun kind -> eligibility kind claim |> Result.isOk)
            |> List.map CommandKinds.token
