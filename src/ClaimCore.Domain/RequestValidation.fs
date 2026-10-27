namespace ClaimCore.Domain

open System

/// Permanent parsing accumulates independent fields before state-dependent admission begins.
module internal RequestValidation =
    let private reference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let private reason value =
        Reason.parse value
        |> Result.mapError (fun violation -> Refusal.invalidInput (InputTarget.Reason, violation))

    let private amend parse =
        function
        | Amendment.Keep -> Ok Amendment.Keep
        | Amendment.Replace value -> parse value |> Result.map Amendment.Replace

    let private change parse =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Replace value -> parse value |> Result.map Change.Replace

    let private payment (input: PaymentInput) =
        Validation.date BusinessField.PaymentDate input.PaymentDate
        |> Result.map (fun date -> { Payment.PaymentDate = date })

    let private act (input: AuthoredCaseAct) : Result<ValidCaseAct, Refusal> =
        match input with
        | CaseAct.Correct value ->
            Admission.map2
                (fun (registration, decision) payment ->
                    CaseAct.Correct
                        {
                            Registration = registration
                            Decision = decision
                            Payment = payment
                        })
                (Admission.map2
                    (fun registration decision -> registration, decision)
                    (amend Validation.registration value.Registration)
                    (change Validation.decision value.Decision))
                (change payment value.Payment)
        | input -> CaseAct.traverse Validation.registration Validation.decision payment reason input

    let parse (request: UnvalidatedCaseRequest) =
        let identity =
            if request.OperationId = Guid.Empty then
                Validation.invalid InputTarget.OperationId Violation.OperationIdRequired
            else
                Ok()

        let revision =
            if request.ExpectedRevision < 0L || request.ExpectedRevision > Revision.Maximum then
                Validation.invalid InputTarget.ExpectedRevision Violation.ExpectedRevisionRange
            else
                Ok()

        Admission.map2
            (fun reference (_, act) -> reference, act)
            (Admission.map2
                (fun () reference -> reference)
                identity
                (reference request.CaseReference))
            (Admission.map2 (fun () act -> (), act) revision (act request.Act))
