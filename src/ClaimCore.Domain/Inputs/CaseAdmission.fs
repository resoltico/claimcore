namespace ClaimCore.Domain.Inputs

open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal CaseInput =
    let invalid field violation =
        Error(Refusal.invalidInput (field, violation))

/// Accepted structural invariants and current admission have distinct inputs and lifetimes.
module internal CaseAdmission =
    let crossFinancial (decision: Decision option) (position: FinancialPosition) =
        match decision with
        | None when position.PaymentCount > 0L ->
            Error(Refusal.Case CaseRefusal.DecisionHasPayments)
        | Some value when Amount.value position.PaidAmount > Money.value (Decision.payable value) ->
            CaseInput.invalid
                (InputTarget.Field BusinessField.PayableAmount)
                Violation.PaymentExceedsPayable
        | _ -> Ok()

    let decisionChange (before: FinancialPosition) after =
        match before.Decision, after with
        | Some previous, Some next when
            before.PaymentCount > 0L
            && Money.currency (Decision.payable previous)
               <> Money.currency (Decision.payable next)
            ->
            CaseInput.invalid
                (InputTarget.Field BusinessField.PayableCurrency)
                Violation.PaymentCurrencyMismatch
        | _ -> Ok()

    let financial (position: FinancialPosition) =
        crossFinancial position.Decision position
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                let issue = InputIssues.head issues in
                RecordViolation.InvalidScalar(issue.Target, issue.Violation)
            | _ -> RecordViolation.InvalidFinancialPosition)

    /// Equality with accepted state preserves existing reassertion semantics; omission never re-admits.
    let fieldAdmission today (before: CaseContent option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues claim.Registration claim.Finance.Decision |> Map.ofList)
            |> Option.defaultValue Map.empty

        fun field value ->
            if Map.tryFind field previous = Some value then
                Ok value
            else
                FieldValue.admit today value
