module ClaimCore.Domain.Tasks.Case.RecordPayment

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let internal paymentInputs =
    group TaskInputMode.Required RecordGroup.Payment
    @ [
        { required (target InputTarget.PaymentDirection) with
            DefaultText = Some(PaymentDirection.token PaymentDirection.defaultValue)
        }
        optional (target InputTarget.PaymentReference)
        optional (target InputTarget.PaymentPayee)
    ]

let declaration =
    family
        "case.recordPayment"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.RecordPayment ]
        (operation :: caseTarget :: revision :: paymentInputs)

let private capability = List.exactlyOne declaration.Capabilities

let internal parseDirection value =
    Vocabulary.tryParse PaymentDirection.vocabulary value
    |> Option.map Ok
    |> Option.defaultValue (Error Violation.PaymentDirectionFormat)

let internal parsePayee value =
    match System.Guid.TryParseExact(value, "D") with
    | true, id ->
        PartyId.create id
        |> Option.map Ok
        |> Option.defaultValue (Error Violation.PayeeUnavailable)
    | _ -> Error Violation.PayeeUnavailable

let internal parse admitReference admit inputs =
    let direction =
        Parsing.optionalText InputTarget.PaymentDirection inputs
        |> Result.bind (function
            | None -> Ok PaymentDirection.defaultValue
            | Some value -> parseDirection value |> Parsing.at InputTarget.PaymentDirection)

    let optional target parse =
        Parsing.optionalText target inputs
        |> Result.bind (function
            | None -> Ok None
            | Some value -> parse value |> Parsing.at target |> Result.map Some)

    let payee = optional InputTarget.PaymentPayee parsePayee

    let details =
        Admission.map2
            (fun direction reference -> direction, reference)
            direction
            (optional InputTarget.PaymentReference (Identifier.parse >> Result.bind admitReference))

    Admission.map2
        (fun payment ((direction, reference), payee) ->
            Payment.withDetails direction reference payee payment)
        (CaseParsing.payment admit (CaseParsing.values inputs))
        (Admission.map2 (fun details payee -> details, payee) details payee)

let check inputs =
    Parsing.check (parse Ok (fun _ -> Ok)) inputs

let private gate = FamilyRules.active false

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let recommendation =
        state
        |> Option.filter (fun (state: DecisionFacts) -> state.Case.Value.Handling = Handling.Open)
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.bind (fun content ->
            match CaseProgress.ofFinancial content.Finance with
            | CaseProgress.Decided
            | CaseProgress.PartlyPaid -> Some 0
            | _ -> None)

    { offered with
        Recommendation = recommendation
    }

let internal admitPayee (state: DecisionFacts) payee =
    let valid =
        payee
        |> Option.forall (fun id ->
            Map.tryFind id state.Parties
            |> Option.exists (fun part -> not part.Value.Removed && part.Value.Content.IsSome))

    if valid then
        Ok()
    else
        Refusal.invalidInput (InputTarget.PaymentPayee, Violation.PayeeUnavailable)
        |> Rejection.ofRefusal
        |> Error

let internal failure =
    function
    | FinancialPositionIssue.TotalOutOfRange ->
        Rejection.ofRefusal (
            Refusal.invalidInput (
                InputTarget.Field BusinessField.PaidAmount,
                Violation.PaymentTotalRange
            )
        )
    | FinancialPositionIssue.CountOutOfRange ->
        Rejection.ofRefusal (
            Refusal.invalidInput (
                InputTarget.Group RecordGroup.Payment,
                Violation.PaymentCountLimit
            )
        )
    | _ -> Rejection.InvalidState RecordViolation.InvalidFinancialPosition

let decide (context: TaskContext) environment (state: DecisionFacts option) binding inputs =
    let syntax = parse Ok (fun _ -> Ok) inputs

    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.Case _, Some today ->
        result {
            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject


            let admit = CaseAdmission.fieldAdmission today None

            let! payment = parse Identifier.admit admit inputs |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state
            do! admitPayee state (Payment.payee payment)

            do!
                if Map.containsKey context.Stamp.OperationId state.Payments then
                    Error(Rejection.Refused Refusal.IdempotencyConflict)
                else
                    Ok()

            let entry = PaymentEntry.recorded context.Stamp.OperationId payment

            let! finance = FinancialPosition.add entry content.Finance |> Result.mapError failure

            return
                [
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value context.Stamp.OperationId)
                        Revision.first
                        (PartValue.Payment { Removed = false; Entry = Some entry })
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content = Some { content with Finance = finance }
                        }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _, _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.Case _ ->
        CaseRules.admit context environment state binding capability (FamilyRules.active false)
        |> Parsing.reject
        |> Result.map (fun () ->
            [
                FamilyRules.change
                    context
                    state.Id
                    (OperationId.value context.Stamp.OperationId)
                    Revision.first
                    (PartValue.Payment { Removed = false; Entry = None })
                CaseRules.change context state { state.Case.Value with Content = None }
            ])
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun principal access caseId _ ->
                    match caseId with
                    | Some id ->
                        FamilyRules.permissionFor principal access (Scope.Case id) capability
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
