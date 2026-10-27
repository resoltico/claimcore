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

    let private act (input: AuthoredCaseAct) : Result<ValidCaseAct, Refusal> =
        match input with
        | CaseAct.Correct { Registration = Change.Clear } -> Error Refusal.ActNotPermitted
        | _ ->
            CaseAct.traverse
                Validation.registration
                Validation.decision
                (fun (input: PaymentInput) ->
                    Validation.date BusinessField.PaymentDate input.PaymentDate
                    |> Result.map (fun date -> { Payment.PaymentDate = date }))
                reason
                input

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
