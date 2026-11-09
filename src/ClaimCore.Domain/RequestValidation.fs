namespace ClaimCore.Domain

open System

/// Retained parsing accumulates independent fields before state-dependent admission begins.
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
        | Amendment.Update value -> parse value |> Result.map Amendment.Update

    let private change parse =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Update value -> parse value |> Result.map Change.Update

    let private act admit admitReason (input: AuthoredCaseAct) : Result<ValidCaseAct, Refusal> =
        match input with
        | CaseAct.Correct value ->
            Admission.map2
                (fun registration decision ->
                    CaseAct.Correct
                        {
                            Registration = registration
                            Decision = decision
                        })
                (amend
                    (RegistrationUpdateInput.parseWith admit >> InputValidation.group)
                    value.Registration)
                (change
                    (DecisionUpdateInput.parseWith admit >> InputValidation.group)
                    value.Decision)
        | input ->
            CaseAct.traverse
                (Registration.parseWith admit >> InputValidation.group)
                (RegistrationUpdateInput.parseWith admit >> InputValidation.group)
                (Decision.parseWith admit >> InputValidation.group)
                (DecisionUpdateInput.parseWith admit >> InputValidation.group)
                (Payment.parseWith admit >> InputValidation.group)
                (fun raw ->
                    OperationId.create raw
                    |> Option.map Ok
                    |> Option.defaultWith (fun () ->
                        InputValidation.invalid InputTarget.PaymentId Violation.PaymentIdRequired))
                (fun raw -> reason raw |> Result.bind admitReason)
                input

    let private identity operationId =
        OperationId.create operationId
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            InputValidation.invalid InputTarget.OperationId Violation.OperationIdRequired)

    let private expected expectedRevision =
        Revision.create expectedRevision
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            InputValidation.invalid InputTarget.ExpectedRevision Violation.ExpectedRevisionRange)

    let private parts admit admitReason operationId referenceResult expectedRevision input =
        let identity = identity operationId
        let revision = expected expectedRevision

        Admission.map2
            (fun (identity, reference) (revision, act) -> reference, identity, revision, act)
            (Admission.map2 (fun identity reference -> identity, reference) identity referenceResult)
            (Admission.map2
                (fun revision act -> revision, act)
                revision
                (act admit admitReason input))

    let parseWith admit admitReason (request: UnvalidatedCaseRequest) =
        parts
            admit
            admitReason
            request.OperationId
            (reference request.CaseReference)
            request.ExpectedRevision
            request.Act

    let private syntaxAdmission _field =
        FieldValue.admit System.DateOnly.MaxValue

    let private syntaxReason =
        Reason.admit
        >> Result.mapError (fun violation -> Refusal.invalidInput (InputTarget.Reason, violation))

    let syntax operationId authoredReference expectedRevision input =
        let reference =
            match authoredReference with
            | Some raw -> reference raw |> Result.map Some
            | None -> Ok None

        parts syntaxAdmission syntaxReason operationId reference expectedRevision input
        |> Result.map ignore

    let parse request = parseWith (fun _ -> Ok) Ok request


    /// Only supplied request metadata participates; field and chronology rules remain shared.
    let syntaxIntent operationId authoredReference expectedRevision input =
        let supplied check =
            Option.map (check >> Result.map ignore) >> Option.defaultValue (Ok())

        Admission.collect
            [
                supplied identity operationId
                supplied reference authoredReference
                supplied expected expectedRevision
                act syntaxAdmission syntaxReason input |> Result.map ignore
            ]
