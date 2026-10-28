namespace ClaimCore.Domain


open ClaimCore.Domain.ResultFlow

/// Correction choices are parsed once; KEEP takes typed accepted state under the caller's lock.
module internal CaseCorrections =
    let private decisionChoice progress choice =
        let current = PaymentProgress.decision progress

        match choice, current with
        | Change.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | Change.Replace replacement, Some _ -> Ok(Some replacement)
        | Change.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        let current = PaymentProgress.paymentDate progress

        match choice, current with
        | Change.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | Change.Replace replacement, Some _ -> Ok(Some replacement.PaymentDate)
        | Change.Clear, Some _ -> Ok None

    /// Unary presence advice shares the execution guards; combinations are still decided by apply.
    let availableActions progress group =
        let present =
            match group with
            | RecordGroup.Registration -> true
            | RecordGroup.Decision -> decisionChoice progress Change.Clear |> Result.isOk
            | RecordGroup.Payment -> paymentChoice progress Change.Clear |> Result.isOk

        match group with
        | RecordGroup.Registration -> Amendment.actions
        | RecordGroup.Decision
        | RecordGroup.Payment ->
            if present then
                CorrectionGroupAction.all
            else
                [ CorrectionGroupAction.Keep ]

    let private paidAcknowledgement progress (correction: ValidCorrection) =
        match progress, correction.Decision, correction.Payment with
        | PaymentProgress.Paid _, Change.Clear, payment when payment <> Change.Clear ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentClearRequired
        | PaymentProgress.Paid _, Change.Replace _, Change.Keep ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentAcknowledgementRequired
        | _ -> Ok()

    /// A valid proposed state can be checked independently of acknowledgement input flags.
    let proposed facts progress (correction: ValidCorrection) =
        result {
            let facts =
                match correction.Registration with
                | Amendment.Keep -> facts
                | Amendment.Replace replacement -> replacement

            let! decision = decisionChoice progress correction.Decision
            let! payment = paymentChoice progress correction.Payment

            let! next =
                PaymentProgress.fromGroups decision payment
                |> Result.mapError (fun () -> Refusal.DecisionRequired)

            return facts, next
        }

    let apply facts progress (correction: ValidCorrection) =
        result {
            let nextFacts =
                match correction.Registration with
                | Amendment.Keep -> facts
                | Amendment.Replace replacement -> replacement

            let! nextDecision = decisionChoice progress correction.Decision
            let! nextPayment = paymentChoice progress correction.Payment
            do! paidAcknowledgement progress correction

            let! nextProgress =
                PaymentProgress.fromGroups nextDecision nextPayment
                |> Result.mapError (fun () -> Refusal.DecisionRequired)

            if nextFacts = facts && nextProgress = progress then
                return! Error Refusal.CorrectionNoChanges
            else
                return nextFacts, nextProgress
        }
