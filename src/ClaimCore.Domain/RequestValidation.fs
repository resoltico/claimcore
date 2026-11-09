namespace ClaimCore.Domain

/// Preparation parses authored scalars once. Current admission consumes the retained typed results.
module internal RequestValidation =
    [<NoEquality; NoComparison>]
    type private AdmissionContext =
        {
            Field: BusinessField -> Admitted -> Result<Admitted, Violation>
            Reason: Reason -> Result<Reason, Refusal>
        }

    [<NoEquality; NoComparison>]
    type private PreparedValue<'value> =
        {
            Build: AdmissionContext -> Result<'value, Refusal>
            Dates: (BusinessField * CalendarDate option) list
        }

    type private PreparedAct =
        CaseAct<
            PreparedValue<Registration>,
            PreparedValue<ValidRegistrationUpdate>,
            PreparedValue<Decision>,
            PreparedValue<ValidDecisionUpdate>,
            PreparedValue<Payment>,
            PreparedValue<OperationId>,
            PreparedValue<Reason>
         >

    [<NoEquality; NoComparison>]
    type Prepared =
        private
            {
                Source: UnvalidatedCaseRequest
                Reference: Result<CaseReference, Refusal>
                Identity: Result<OperationId, Refusal>
                Revision: Result<Revision, Refusal>
                Act: PreparedAct
                Syntax: Result<CaseReference * OperationId * Revision * ValidCaseAct, Refusal>
            }

    let private reference value =
        CaseReference.parse value
        |> Result.mapError (fun violation ->
            Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let private reason value =
        Reason.parse value
        |> Result.mapError (fun violation -> Refusal.invalidInput (InputTarget.Reason, violation))

    let private identity value =
        OperationId.create value
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            Error(Refusal.invalidInput (InputTarget.OperationId, Violation.OperationIdRequired)))

    let private expected value =
        Revision.create value
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            Error(
                Refusal.invalidInput (
                    InputTarget.ExpectedRevision,
                    Violation.ExpectedRevisionRange
                )
            ))

    let private dates parsed =
        parsed
        |> List.choose (fun (field, value) ->
            if (BusinessField.describe field).Kind = FieldValueKind.CalendarDate then
                Some(
                    field,
                    value |> Result.toOption |> Option.flatten |> Option.map FieldValue.date
                )
            else
                None)

    let private group parse parsed =
        {
            Build = fun context -> parse context.Field parsed |> InputValidation.group
            Dates = parsed |> List.map (fun (field, value) -> field, Result.map Some value) |> dates
        }

    let private update parse parsed =
        {
            Build = fun context -> parse context.Field parsed |> InputValidation.group
            Dates = dates parsed
        }

    let private prepareAct input : PreparedAct =
        CaseAct.map
            (RegistrationInput.parsed >> group Registration.parsePreparedWith)
            (RegistrationUpdateInput.prepare
             >> update RegistrationUpdateInput.parsePreparedWith)
            (DecisionInput.parsed >> group Decision.parsePreparedWith)
            (DecisionUpdateInput.prepare >> update DecisionUpdateInput.parsePreparedWith)
            (PaymentInput.parsed >> group Payment.parsePreparedWith)
            (fun raw ->
                let parsed =
                    OperationId.create raw
                    |> Option.map Ok
                    |> Option.defaultWith (fun () ->
                        Error(
                            Refusal.invalidInput (
                                InputTarget.PaymentId,
                                Violation.PaymentIdRequired
                            )
                        ))

                {
                    Build = (fun _ -> parsed)
                    Dates = []
                })
            (fun raw ->
                let parsed = reason raw

                {
                    Build = (fun context -> parsed |> Result.bind context.Reason)
                    Dates = []
                })
            input

    let private amend build =
        function
        | Amendment.Keep -> Ok Amendment.Keep
        | Amendment.Update input -> build input |> Result.map Amendment.Update

    let private change build =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Update input -> build input |> Result.map Change.Update

    let private buildAct context =
        function
        | CaseAct.Correct value ->
            Admission.map2
                (fun registration decision ->
                    CaseAct.Correct
                        {
                            Registration = registration
                            Decision = decision
                        })
                (amend (fun value -> value.Build context) value.Registration)
                (change (fun value -> value.Build context) value.Decision)
        | act ->
            CaseAct.traverse
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                (fun value -> value.Build context)
                act

    let private parts id reference revision act =
        Admission.map2
            (fun (id, reference) (revision, act) -> reference, id, revision, act)
            (Admission.map2 (fun id reference -> id, reference) id reference)
            (Admission.map2 (fun revision act -> revision, act) revision act)

    let prepare (request: UnvalidatedCaseRequest) =
        let id = identity request.OperationId
        let reference = reference request.CaseReference
        let revision = expected request.ExpectedRevision
        let act = prepareAct request.Act

        {
            Source = request
            Reference = reference
            Identity = id
            Revision = revision
            Act = act
            Syntax =
                buildAct { Field = (fun _ -> Ok); Reason = Ok } act
                |> parts id reference revision
        }

    let source prepared = prepared.Source
    let parsed prepared = prepared.Syntax

    let admitWith admit admitReason prepared =
        buildAct { Field = admit; Reason = admitReason } prepared.Act
        |> parts prepared.Identity prepared.Reference prepared.Revision

    let private chronologyAct act =
        let assertions =
            match act with
            | CaseAct.Open value -> value.Dates
            | CaseAct.AmendRegistration value -> value.Dates
            | CaseAct.Decide value -> value.Dates
            | CaseAct.RecordPayment value -> value.Dates
            | CaseAct.Correct correction ->
                (match correction.Registration with
                 | Amendment.Keep -> []
                 | Amendment.Update value -> value.Dates)
                @ (match correction.Decision with
                   | Change.Keep
                   | Change.Clear -> []
                   | Change.Update value -> value.Dates)
            | CaseAct.Bare _
            | CaseAct.RemovePayment _
            | CaseAct.Void _
            | CaseAct.Reinstate _ -> []

        let opening = CaseAct.kind act = CaseActKind.Open

        let clearing =
            match act with
            | CaseAct.Correct { Decision = Change.Clear } -> true
            | _ -> false

        opening, clearing, assertions

    let chronology prepared = chronologyAct prepared.Act

    let private syntaxAdmission _field =
        FieldValue.admit System.DateOnly.MaxValue

    let private syntaxReason =
        Reason.admit
        >> Result.mapError (fun violation -> Refusal.invalidInput (InputTarget.Reason, violation))

    /// Only supplied metadata participates; authored values retain their own independent checks.
    let syntaxIntent operationId authoredReference expectedRevision input =
        let supplied check =
            Option.map (check >> Result.map ignore) >> Option.defaultValue (Ok())

        let prepared = prepareAct input
        let opening, clearing, assertions = chronologyAct prepared

        Admission.collect
            [
                supplied identity operationId
                supplied reference authoredReference
                supplied expected expectedRevision
                buildAct
                    {
                        Field = syntaxAdmission
                        Reason = syntaxReason
                    }
                    prepared
                |> Result.map ignore
                StateValidation.preparedChronology None opening clearing assertions
            ]
