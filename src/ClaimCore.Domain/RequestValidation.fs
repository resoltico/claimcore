namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Admission of an unaccepted command is independent of accepted case state.
module internal RequestValidation =
    let controls operationId reference expectedVersion =
        result {
            do! Validation.text InputTarget.CaseReference reference |> Result.map ignore

            if operationId = Guid.Empty then
                return!
                    Validation.invalidCommand
                        InputTarget.OperationId
                        CommandViolation.EmptyOperationId
            elif expectedVersion < 0L || expectedVersion = Int64.MaxValue then
                return!
                    Validation.invalidCommand
                        InputTarget.ExpectedVersion
                        CommandViolation.ExpectedVersionOutOfRange
            else
                return ()
        }

    /// Envelope and structural payload checks run before identity encoding and again at decision time.
    let parse (request: CommandRequest) =
        result {
            do! controls request.OperationId request.CaseReference request.ExpectedVersion

            match request.Command with
            | Command.Open input ->
                return! Validation.registration input |> Result.map ValidatedCommand.Open
            | Command.AmendRegistration input ->
                return!
                    Validation.registration input |> Result.map ValidatedCommand.AmendRegistration
            | Command.CorrectCase correction ->
                return! CaseCorrections.parse correction |> Result.map ValidatedCommand.CorrectCase
            | Command.Decide input ->
                return! Validation.decision input |> Result.map ValidatedCommand.Decide
            | Command.RecordPayment value ->
                return!
                    Validation.date InputTarget.PaymentDate value
                    |> Result.map ValidatedCommand.RecordPayment
            | Command.WithdrawDecision -> return ValidatedCommand.WithdrawDecision
            | Command.ClearPayment -> return ValidatedCommand.ClearPayment
            | Command.Close -> return ValidatedCommand.Close
            | Command.Reopen -> return ValidatedCommand.Reopen
        }
