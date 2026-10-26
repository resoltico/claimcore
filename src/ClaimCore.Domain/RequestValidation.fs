namespace ClaimCore.Domain


open System
open ClaimCore.Domain.ResultFlow

/// Admission of an unaccepted act is independent of accepted case state.
module internal RequestValidation =
    let controls operationId reference expectedRevision =
        result {
            do!
                Validation.text (InputTarget.Field BusinessField.CaseReference) reference
                |> Result.map ignore

            if operationId = Guid.Empty then
                return! Validation.invalid InputTarget.OperationId Violation.OperationIdRequired
            elif expectedRevision < 0L || expectedRevision > Revision.Maximum then
                return!
                    Validation.invalid InputTarget.ExpectedRevision Violation.ExpectedRevisionRange
            else
                return ()
        }

    /// Business payload parsing checks permanent format only.
    let business act =
        match act with
        | CaseAct.Open input -> Validation.registration input |> Result.map ValidatedAct.Open
        | CaseAct.AmendRegistration input ->
            Validation.registration input |> Result.map ValidatedAct.AmendRegistration
        | CaseAct.Correct correction ->
            CaseCorrections.parse correction |> Result.map ValidatedAct.Correct
        | CaseAct.Decide input -> Validation.decision input |> Result.map ValidatedAct.Decide
        | CaseAct.RecordPayment value ->
            Validation.date BusinessField.PaymentDate value.PaymentDate
            |> Result.map ValidatedAct.RecordPayment
        | CaseAct.WithdrawDecision -> Ok ValidatedAct.WithdrawDecision
        | CaseAct.ClearPayment -> Ok ValidatedAct.ClearPayment
        | CaseAct.Close -> Ok ValidatedAct.Close
        | CaseAct.Reopen -> Ok ValidatedAct.Reopen
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Erase -> invalidArg (nameof act) "Expected a business act."


    let parse (request: UnvalidatedCaseRequest) =
        result {
            do! controls request.OperationId request.CaseReference request.ExpectedRevision

            match request.Act with
            | CaseAct.Void reason
            | CaseAct.Reinstate reason ->
                return! Validation.text InputTarget.Reason reason |> Result.map ignore
            | CaseAct.Erase -> return ()
            | act -> return! business act |> Result.map ignore
        }
