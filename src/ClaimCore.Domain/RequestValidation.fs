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
            OperationId.create request.OperationId
            |> Option.map Ok
            |> Option.defaultWith (fun () ->
                Validation.invalid InputTarget.OperationId Violation.OperationIdRequired)

        let revision =
            Revision.create request.ExpectedRevision
            |> Option.map Ok
            |> Option.defaultWith (fun () ->
                Validation.invalid InputTarget.ExpectedRevision Violation.ExpectedRevisionRange)

        Admission.map2
            (fun (identity, reference) (revision, act) -> reference, identity, revision, act)
            (Admission.map2
                (fun identity reference -> identity, reference)
                identity
                (reference request.CaseReference))
            (Admission.map2 (fun revision act -> revision, act) revision (act request.Act))
