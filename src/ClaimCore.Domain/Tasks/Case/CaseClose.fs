module ClaimCore.Domain.Tasks.Case.Close

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.close"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.Close ]
        [
            operation
            caseTarget
            revision
            required (field BusinessField.Outcome) |> retained
            optional (field BusinessField.ClosingNote)
        ]

let private capability = List.exactlyOne declaration.Capabilities

let private outcome inputs =
    let field = InputTarget.Field BusinessField.Outcome

    Parsing.scalar
        field
        (fun value ->
            Vocabulary.tryParse ClosureOutcome.vocabulary value
            |> Option.map Ok
            |> Option.defaultValue (Error Violation.ClosureOutcomeFormat))
        inputs

let private note admit inputs =
    let field = InputTarget.Field BusinessField.ClosingNote

    Parsing.optionalText field inputs
    |> Result.bind (function
        | None -> Ok None
        | Some value ->
            NoteText.parse value |> Result.bind admit |> Parsing.at field |> Result.map Some)

let private parse admit inputs =
    Admission.map2
        (fun outcome note ->
            {
                Closure.Outcome = outcome
                Note = note
            })
        (outcome inputs)
        (note admit inputs)

let check inputs = Parsing.check (parse Ok) inputs

let private gate =
    CaseRules.gate false (fun state -> state.Case.Value.Handling = Handling.Open)

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let recommendation, outcome =
        state
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.bind (fun content ->
            let assessment = FinancialAssessment.ofPosition None content.Finance

            if assessment.NeedsReview then
                None
            else
                assessment.Comparison)
        |> Option.map (fun totals ->
            let payable = DecimalTotal.ofAmount (Money.amount totals.Payable)

            if payable = DecimalTotal.zero then
                Some 0, Some ClosureOutcome.Declined
            elif totals.NetPaid >= payable then
                Some 0, Some ClosureOutcome.Settled
            else
                None, None)
        |> Option.defaultValue (None, None)

    { offered with
        Recommendation = recommendation
        Seeds =
            outcome
            |> Option.map (fun outcome ->
                Map.ofList
                    [ field BusinessField.Outcome, InputValue.Text(ClosureOutcome.token outcome) ])
            |> Option.defaultValue Map.empty
    }

let private accept context environment (state: DecisionFacts option) binding parsed =
    match state, binding with
    | Some state, Binding.Case _ ->
        CaseRules.admit context environment state binding capability gate
        |> Result.bind (fun () -> parsed)
        |> Result.map (fun closure ->
            [
                CaseRules.change
                    context
                    state
                    { state.Case.Value with
                        Handling = Handling.Closed closure
                    }
            ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decide context environment state binding inputs =
    accept context environment state binding (parse NoteText.admit inputs)

let decideRetained context environment state binding inputs =
    accept
        context
        environment
        state
        binding
        (outcome inputs |> Result.map (fun value -> { Outcome = value; Note = None }))

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
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
