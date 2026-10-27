namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Permanent parsing is independent of accepted state and today's admission.
module internal RequestValidation =
    let private reference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation))

    let private reason value =
        Reason.parse value
        |> Result.mapError (fun violation -> Refusal.InvalidInput(InputTarget.Reason, violation))

    let private act =
        function
        | UnvalidatedCaseAct.Open input -> Validation.registration input |> Result.map CaseAct.Open
        | UnvalidatedCaseAct.AmendRegistration input ->
            Validation.registration input |> Result.map CaseAct.AmendRegistration
        | UnvalidatedCaseAct.Correct input ->
            CaseCorrections.parse input |> Result.map CaseAct.Correct
        | UnvalidatedCaseAct.Decide input -> Validation.decision input |> Result.map CaseAct.Decide
        | UnvalidatedCaseAct.RecordPayment input ->
            Validation.date BusinessField.PaymentDate input.PaymentDate
            |> Result.map CaseAct.RecordPayment
        | UnvalidatedCaseAct.WithdrawDecision -> Ok(CaseAct.WithdrawDecision)
        | UnvalidatedCaseAct.ClearPayment -> Ok(CaseAct.ClearPayment)
        | UnvalidatedCaseAct.Close -> Ok(CaseAct.Close)
        | UnvalidatedCaseAct.Reopen -> Ok(CaseAct.Reopen)
        | UnvalidatedCaseAct.Void input -> reason input |> Result.map CaseAct.Void
        | UnvalidatedCaseAct.Reinstate input -> reason input |> Result.map CaseAct.Reinstate
        | UnvalidatedCaseAct.Erase -> Ok CaseAct.Erase

    let parse (request: UnvalidatedCaseRequest) =
        result {
            let! reference = reference request.CaseReference

            if request.OperationId = Guid.Empty then
                return! Validation.invalid InputTarget.OperationId Violation.OperationIdRequired
            elif request.ExpectedRevision < 0L || request.ExpectedRevision > Revision.Maximum then
                return!
                    Validation.invalid InputTarget.ExpectedRevision Violation.ExpectedRevisionRange
            else
                let! act = act request.Act
                return reference, act
        }
