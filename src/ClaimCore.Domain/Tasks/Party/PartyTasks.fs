module internal ClaimCore.Domain.Tasks.Party.PartyTasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let private role = InputTarget.PartyRole
let private name = InputTarget.PartyName
let private details = InputTarget.PartyDetails

let fields =
    [ required (target role); optional (target name); optional (target details) ]

let private updateFields =
    fields
    |> List.map (fun field ->
        { field with
            Mode = TaskInputMode.Update
            Clearable = field.Input <> target role
        })

let private parseRole value =
    Vocabulary.tryParse PartyRole.vocabulary value
    |> Option.map Ok
    |> Option.defaultValue (Error Violation.PartyRoleRequired)

let private admit retained check value =
    if retained then Ok value else check value

let private optionalValue input parse check retained values =
    Parsing.optionalText input values
    |> Result.bind (function
        | None -> Ok None
        | Some value ->
            parse value
            |> Result.bind (admit retained check)
            |> Parsing.at input
            |> Result.map Some)

let private content selectedRole selectedName selectedDetails =
    Admission.map2
        (fun (role, name) details -> role, name, details)
        (Admission.map2 (fun role name -> role, name) selectedRole selectedName)
        selectedDetails
    |> Result.bind (fun (role, name, details) ->
        PartyContent.create role name details |> Parsing.at InputTarget.PartyName)

let parse retained values =
    content
        (Parsing.scalar role parseRole values)
        (optionalValue name PartyName.parse PartyName.admit retained values)
        (optionalValue details PartyDetails.parse PartyDetails.admit retained values)

let rows (values: InputMap) =
    Map.tryFind TaskCollectionId.Parties values.Collections
    |> Option.defaultValue []

let collection =
    let members, missing = PartyContent.requiredContent

    let input =
        function
        | PartyContentMember.Name -> target name
        | PartyContentMember.Details -> target details

    {
        Collection = TaskCollectionId.Parties
        MinimumRows = 0
        Inputs = fields
        RowConstraints = [ TaskRowConstraint.AtLeastOne(List.map input members, missing) ]
    }

let parseRows retained values =
    let admitRow (row: InputRow) =
        let values = InputMap.create row.Values Map.empty |> Option.get

        match parse retained values with
        | Ok party -> Ok(row.Ordinal, party)
        | Error refusal ->
            match Rejection.ofRefusal refusal with
            | Rejection.Input issues ->
                TaskInputIssues.toList issues
                |> List.map (fun issue -> { issue with Row = Some row.Target })
                |> Error
            | _ -> invalidOp "Party row parse returned a non-input refusal."

    let results = rows values |> List.map admitRow

    let issues =
        results
        |> List.collect (function
            | Error issues -> issues
            | _ -> [])

    match TaskInputIssues.ofList issues with
    | Some issues -> Error(Rejection.Input issues)
    | None ->
        results
        |> List.choose (function
            | Ok row -> Some row
            | _ -> None)
        |> Ok

let initial context caseId party value =
    FamilyRules.change
        context
        caseId
        (PartyId.value party)
        Revision.first
        (PartValue.Party { Removed = false; Content = value })

let private capability = Capability.Register

let private authorize principal access caseId _ =
    match caseId with
    | Some id ->
        let scope = Scope.Case id

        FamilyRules.permissionFor principal access scope Capability.ReadCase
        |> Result.bind (fun () -> FamilyRules.permissionFor principal access scope capability)
    | None -> Error FamilyRules.unavailable

let private permission (environment: FamilyEnvironment) (state: DecisionFacts) =
    authorize environment.RequesterPrincipal environment.Requester (Some state.Id) InputMap.empty

let private availability declaration environment (state: DecisionFacts option) binding gate =
    match state with
    | Some state ->
        match permission environment state with
        | Error refusal -> Declarations.availability declaration (Some refusal)
        | Ok() ->
            FamilyRules.availability declaration environment (Some state) binding capability gate
    | None -> Declarations.availability declaration (Some FamilyRules.unavailable)

let private admitAction context environment state binding gate =
    FamilyRules.admitPermission
        context
        environment
        state
        binding
        (permission environment state)
        gate

let private stateGate = FamilyRules.active false

let private addGate (state: DecisionFacts) =
    stateGate state
    |> Option.orElseWith (fun () ->
        let count =
            state.Parties
            |> Map.values
            |> Seq.filter (fun part -> not part.Value.Removed)
            |> Seq.length

        if count >= PartyContent.MaximumCurrent then
            Some(Refusal.Case CaseRefusal.PartyLimit)
        else
            None)

let private addDeclaration =
    family
        "party.add"
        ActSubject.Party
        true
        ActFamily.Business
        Consequence.Routine
        [ capability ]
        (operation :: caseTarget :: fields)

let private addDecision
    retained
    (context: TaskContext)
    environment
    (state: DecisionFacts option)
    binding
    values
    =
    match state, binding with
    | Some state, Binding.PartyNew _ ->
        admitAction context environment state binding addGate
        |> Result.bind (fun () ->
            let id = PartyId.ofOperationId context.Stamp.OperationId

            if Map.containsKey id state.Parties then
                Error Refusal.IdempotencyConflict
            else
                let content =
                    if retained then
                        Ok None
                    else
                        parse (context.Decision.Lifetime = DecisionLifetime.Recorded) values
                        |> Result.map Some

                content |> Result.map (fun value -> [ initial context state.Id id value ]))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let private identity =
    [
        required (target InputTarget.PartyId) |> bound
        required (target InputTarget.ExpectedPartyRevision)
    ]

let private selected binding (state: DecisionFacts) =
    match binding with
    | Binding.Party(_, id, _) ->
        Map.tryFind id state.Parties
        |> Option.filter (fun part -> not part.Value.Removed)
    | _ -> None

let private editGate binding state =
    stateGate state
    |> Option.orElseWith (fun () ->
        if (selected binding state).IsSome then
            None
        else
            Some FamilyRules.unavailable)

let private updateValue input parse check retained before values =
    Parsing.workValue input (fun value -> parse value |> Result.bind (admit retained check)) values
    |> Result.map (function
        | WorkValue.Keep -> before
        | WorkValue.Clear -> None
        | WorkValue.Set value -> Some value)

let private updateRole values =
    Parsing.workValue role parseRole values
    |> Result.bind (function
        | WorkValue.Clear -> Error(Parsing.issue role Violation.PartyRoleRequired)
        | value -> Ok value)

let private editParse (before: PartyContent) retained values =
    let selectedRole =
        updateRole values
        |> Result.map (function
            | WorkValue.Set value -> value
            | _ -> before.Role)

    content
        selectedRole
        (updateValue name PartyName.parse PartyName.admit retained before.Name values)
        (updateValue details PartyDetails.parse PartyDetails.admit retained before.Details values)

let private editCheck values =
    let any = Admission.map2 (fun () () -> ())

    Parsing.check
        (fun values ->
            updateRole values
            |> Result.map ignore
            |> any (
                updateValue name PartyName.parse PartyName.admit true None values
                |> Result.map ignore
            )
            |> any (
                updateValue details PartyDetails.parse PartyDetails.admit true None values
                |> Result.map ignore
            ))
        values

let private editDecision
    removed
    retained
    (context: TaskContext)
    environment
    (state: DecisionFacts option)
    binding
    values
    =
    match state, binding with
    | Some state, Binding.Party(_, id, expected) ->
        let gate (state: DecisionFacts) =
            if state.Case.Value.Lifecycle = Lifecycle.Erased then
                stateGate state
            else
                match Map.tryFind id state.Parties with
                | Some prior ->
                    match FamilyRules.revision expected prior.Revision with
                    | Error refusal -> Some refusal
                    | Ok() -> editGate binding state
                | None -> editGate binding state

        admitAction context environment state binding gate
        |> Result.bind (fun () ->
            let prior = Map.find id state.Parties

            (if removed || retained then
                 Ok prior.Value.Content
             else
                 match prior.Value.Content with
                 | None -> Error FamilyRules.unavailable
                 | Some prior ->
                     editParse prior (context.Decision.Lifetime = DecisionLifetime.Recorded) values
                     |> Result.bind (fun changed ->
                         if prior = changed then
                             Error(Refusal.Case CaseRefusal.CorrectionNoChanges)
                         else
                             Ok(Some changed)))
            |> Result.map (fun content ->
                [
                    FamilyRules.change
                        context
                        state.Id
                        (PartyId.value id)
                        (Revision.successor prior.Revision)
                        (PartValue.Party { Removed = removed; Content = content })
                ]))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let add =
    Task.Family
        {
            Declaration = addDeclaration
            Authorize = authorize
            Check = Parsing.check (parse true)
            Availability =
                fun environment state binding ->
                    availability addDeclaration environment state binding addGate
            Decide = addDecision false
            DecideRetained = addDecision true
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }

let private edit token removed inputs =
    let declaration =
        family
            token
            ActSubject.Party
            false
            ActFamily.Business
            Consequence.Routine
            [ capability ]
            (operation :: caseTarget :: identity @ inputs)

    Task.Family
        {
            Declaration = declaration
            Authorize = authorize
            Check = if removed then fun _ -> [] else editCheck
            Availability =
                fun environment state binding ->
                    let offered =
                        availability declaration environment state binding (editGate binding)

                    if removed || offered.Blocked.IsSome then
                        offered
                    else
                        let seeds =
                            state
                            |> Option.bind (selected binding)
                            |> Option.bind _.Value.Content
                            |> Option.map (fun content ->
                                [
                                    target role, InputValue.Text(PartyRole.token content.Role)
                                    if content.Name.IsSome then
                                        target name,
                                        InputValue.Text(PartyName.format content.Name.Value)
                                    if content.Details.IsSome then
                                        target details,
                                        InputValue.Text(PartyDetails.format content.Details.Value)
                                ]
                                |> Map.ofList)
                            |> Option.defaultValue Map.empty

                        { offered with Seeds = seeds }
            Decide = editDecision removed false
            DecideRetained = editDecision removed true
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }

let update = edit "party.update" false updateFields
let remove = edit "party.remove" true []
