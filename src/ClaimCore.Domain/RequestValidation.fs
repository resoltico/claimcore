namespace ClaimCore.Domain

open ClaimCore.Catalog

open System
open ClaimCore.Domain.ResultFlow

/// Admission of an unaccepted command is independent of accepted case state.
module internal RequestValidation =
    let controls operationId reference expectedRevision =
        result {
            do! Validation.text InputTarget.CaseReference reference |> Result.map ignore

            if operationId = Guid.Empty then
                return! Validation.invalidAct InputTarget.OperationId ActViolation.EmptyOperationId
            elif expectedRevision < 0L || expectedRevision > Revision.Maximum then
                return!
                    Validation.invalidAct
                        InputTarget.ExpectedRevision
                        ActViolation.ExpectedRevisionOutOfRange
            else
                return ()
        }

    /// Business payload parsing checks permanent format only.
    let business command =
        match command with
        | BusinessCommand.Open input ->
            Validation.registration input |> Result.map ValidatedCommand.Open
        | BusinessCommand.AmendRegistration input ->
            Validation.registration input |> Result.map ValidatedCommand.AmendRegistration
        | BusinessCommand.CorrectCase correction ->
            CaseCorrections.parse correction |> Result.map ValidatedCommand.CorrectCase
        | BusinessCommand.Decide input ->
            Validation.decision input |> Result.map ValidatedCommand.Decide
        | BusinessCommand.RecordPayment value ->
            Validation.date InputTarget.PaymentDate value
            |> Result.map ValidatedCommand.RecordPayment
        | BusinessCommand.WithdrawDecision -> Ok ValidatedCommand.WithdrawDecision
        | BusinessCommand.ClearPayment -> Ok ValidatedCommand.ClearPayment
        | BusinessCommand.Close -> Ok ValidatedCommand.Close
        | BusinessCommand.Reopen -> Ok ValidatedCommand.Reopen

    let parse (request: CaseRequest) =
        result {
            do! controls request.OperationId request.CaseReference request.ExpectedRevision

            match request.Act with
            | CaseAct.Business command -> return! business command |> Result.map ignore
            | CaseAct.Stewardship(StewardshipAct.Void reason)
            | CaseAct.Stewardship(StewardshipAct.Reinstate reason) ->
                return! Validation.text InputTarget.Reason reason |> Result.map ignore
            | CaseAct.Stewardship StewardshipAct.Erase -> return ()
        }
