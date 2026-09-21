namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Admission of an unaccepted command is independent of accepted case state.
module internal RequestValidation =
    /// Envelope and structural payload checks run before identity encoding and again at decision time.
    let validate (request: CommandRequest) =
        result {
            do!
                Validation.text InputTarget.CaseReference request.CaseReference
                |> Result.map ignore

            if request.OperationId = Guid.Empty then
                return!
                    Validation.invalidCommand
                        InputTarget.OperationId
                        CommandViolation.EmptyOperationId
            elif request.ExpectedVersion < 0L || request.ExpectedVersion = Int64.MaxValue then
                return!
                    Validation.invalidCommand
                        InputTarget.ExpectedVersion
                        CommandViolation.ExpectedVersionOutOfRange
            else
                match request.Command with
                | Command.Open input
                | Command.AmendRegistration input ->
                    return! Validation.registration input |> Result.map ignore
                | Command.CorrectCase correction -> return! CaseCorrections.validate correction
                | Command.Decide input -> return! Validation.decision input |> Result.map ignore
                | Command.RecordPayment value ->
                    return! Validation.date InputTarget.PaymentDate value |> Result.map ignore
                | Command.WithdrawDecision
                | Command.ClearPayment
                | Command.Close
                | Command.Reopen -> return ()
        }
