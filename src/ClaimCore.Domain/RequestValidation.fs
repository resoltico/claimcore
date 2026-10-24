namespace ClaimCore.Domain

open ClaimCore.Contract

open System
open ClaimCore.Domain.ResultFlow

/// Admission of an unaccepted command is independent of accepted case state.
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
    let business command =
        match command with
        | CaseAct.Open input -> Validation.registration input |> Result.map ValidatedCommand.Open
        | CaseAct.AmendRegistration input ->
            Validation.registration input |> Result.map ValidatedCommand.AmendRegistration
        | CaseAct.CorrectCase correction ->
            CaseCorrections.parse correction |> Result.map ValidatedCommand.CorrectCase
        | CaseAct.Decide input -> Validation.decision input |> Result.map ValidatedCommand.Decide
        | CaseAct.RecordPayment value ->
            Validation.date (InputTarget.Field BusinessField.PaymentDate) value.PaymentDate
            |> Result.map ValidatedCommand.RecordPayment
        | CaseAct.WithdrawDecision -> Ok ValidatedCommand.WithdrawDecision
        | CaseAct.ClearPayment -> Ok ValidatedCommand.ClearPayment
        | CaseAct.Close -> Ok ValidatedCommand.Close
        | CaseAct.Reopen -> Ok ValidatedCommand.Reopen
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Erase -> invalidArg (nameof command) "Expected a business act."


    let parse (request: CaseRequest) =
        result {
            do! controls request.OperationId request.CaseReference request.ExpectedRevision

            match request.Act with
            | CaseAct.Void reason
            | CaseAct.Reinstate reason ->
                return! Validation.text InputTarget.Reason reason |> Result.map ignore
            | CaseAct.Erase -> return ()
            | command -> return! business command |> Result.map ignore
        }
