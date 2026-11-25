module ClaimCore.Domain.Tasks.AttachmentTasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks.Declarations

let private capability = Capability.ManageCaseWork

let private addDeclaration =
    { family
          "attachment.add"
          ActSubject.Attachment
          true
          ActFamily.Business
          Consequence.Stewardship
          [ capability ]
          [
              operation
              caseTarget
              required (target InputTarget.AttachmentName)
              required (target InputTarget.AttachmentLength)
              required (target InputTarget.AttachmentDigest)
          ] with
        Execution = TaskExecution.AttachmentUpload
    }

let private withdrawDeclaration =
    family
        "attachment.withdraw"
        ActSubject.Attachment
        false
        ActFamily.Business
        Consequence.Stewardship
        [ capability ]
        [ operation; caseTarget; required (target InputTarget.AttachmentId) |> bound ]

let private authorize principal access caseId _ =
    match caseId with
    | Some id -> CaseWorkPolicy.authorize principal access id
    | None -> Error FamilyRules.unavailable

let private active = FamilyRules.active false

let private addDecision
    context
    (environment: FamilyEnvironment)
    (state: DecisionFacts option)
    binding
    intent
    =
    match state, binding with
    | Some state, Binding.AttachmentNew _ ->
        FamilyRules.admitPermission
            context
            environment
            state
            binding
            (CaseWorkPolicy.authorize environment.RequesterPrincipal environment.Requester state.Id)
            active
        |> Result.bind (fun () -> intent)
        |> Result.map (fun intent ->
            let id = AttachmentId.ofOperationId context.Stamp.OperationId

            let descriptor =
                intent
                |> Option.map (fun intent ->
                    {
                        Blob = id
                        Intent = intent
                        Preview = None
                    })

            [
                FamilyRules.change
                    context
                    state.Id
                    (AttachmentId.value id)
                    Revision.first
                    (PartValue.Attachment(AttachmentEffect.Added(true, descriptor)))
            ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let add =
    Task.Family
        {
            Declaration = addDeclaration
            Authorize = authorize
            Check = Parsing.check AttachmentInputs.admissible
            Availability =
                fun environment state binding ->
                    FamilyRules.availability
                        addDeclaration
                        environment
                        state
                        binding
                        capability
                        active
            Decide =
                fun context environment state binding inputs ->
                    addDecision
                        context
                        environment
                        state
                        binding
                        (AttachmentInputs.admissible inputs |> Result.map Some)
            DecideRetained =
                fun context environment state binding _ ->
                    addDecision context environment state binding (Ok None)
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }

let private selected binding (state: DecisionFacts) =
    match binding with
    | Binding.Attachment(_, id) ->
        Map.tryFind id state.Attachments
        |> Option.bind (fun part ->
            match part.Value with
            | AttachmentEffect.Added _ -> Some(id, part.Revision)
            | AttachmentEffect.Withdrawn -> None)
    | _ -> None

let private withdrawGate binding state =
    active state
    |> Option.orElseWith (fun () ->
        if selected binding state |> Option.isSome then
            None
        else
            Some FamilyRules.unavailable)

let private withdrawDecision
    context
    (environment: FamilyEnvironment)
    (state: DecisionFacts option)
    binding
    _
    =
    match state with
    | Some state ->
        FamilyRules.admitPermission
            context
            environment
            state
            binding
            (CaseWorkPolicy.authorize environment.RequesterPrincipal environment.Requester state.Id)
            (withdrawGate binding)
        |> Result.bind (fun () ->
            match selected binding state with
            | None -> Error FamilyRules.unavailable
            | Some(id, revision) ->
                Ok
                    [
                        FamilyRules.change
                            context
                            state.Id
                            (AttachmentId.value id)
                            (Revision.successor revision)
                            (PartValue.Attachment AttachmentEffect.Withdrawn)
                    ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | None -> Error(Rejection.Refused FamilyRules.unavailable)

let withdraw =
    Task.Family
        {
            Declaration = withdrawDeclaration
            Authorize = authorize
            Check = fun _ -> []
            Availability =
                fun environment state binding ->
                    FamilyRules.availability
                        withdrawDeclaration
                        environment
                        state
                        binding
                        capability
                        (withdrawGate binding)
            Decide = withdrawDecision
            DecideRetained = withdrawDecision
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
