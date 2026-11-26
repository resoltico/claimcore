module ClaimCore.Domain.Tasks.Case.Open

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations
open ClaimCore.Domain.Tasks.Party

let private referenceTarget = InputTarget.Field BusinessField.CaseReference
let private referenceInput = target referenceTarget

let declaration =
    create
        "case.open"
        AggregateKind.Family
        ActSubject.Case
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.Register ]
        (operation
         :: (optional referenceInput |> retained)
         :: revision
         :: group TaskInputMode.Optional RecordGroup.Registration)
        [ PartyTasks.collection ]

let private capability = List.exactlyOne declaration.Capabilities

let private intent policy (inputs: InputMap) =
    match Map.tryFind referenceInput inputs.Values with
    | None -> Ok OpeningIntent.Automatic
    | Some(InputValue.Text value) ->
        CaseReference.parseCustom value
        |> Parsing.at referenceTarget
        |> Result.bind (fun reference ->
            if CaseReferencePolicy.allowsCustom policy then
                Ok(OpeningIntent.Custom reference)
            else
                Error(Parsing.issue referenceTarget Violation.CustomReferenceNotAllowed))
    | Some _ -> Error(Parsing.issue referenceTarget Violation.TextRequired)

let private facts policy admit inputs =
    Admission.map2
        (fun reference registration -> reference, registration)
        (intent policy inputs)
        (CaseParsing.registration admit (CaseParsing.values inputs))

let check inputs =
    let syntax =
        Parsing.check (facts CaseReferencePolicy.CustomAllowed (fun _ -> Ok)) inputs

    match PartyTasks.parseRows true inputs with
    | Error(Rejection.Input issues) -> syntax @ TaskInputIssues.toList issues
    | _ -> syntax

let editableInputs policy =
    TaskDeclaration.editableInputs declaration
    |> List.filter (fun input -> input <> referenceInput || CaseReferencePolicy.allowsCustom policy)

let availability (environment: FamilyEnvironment) (state: DecisionFacts option) binding =
    let permission = FamilyRules.permission environment Scope.Installation capability

    let blocked =
        match permission, binding with
        | Error refusal, _ -> Some refusal
        | _, Binding.CaseNew expected when expected = Revision.zero && state.IsNone ->
            if environment.Maintenance then
                Some Refusal.RegisterMaintenance
            else
                None
        | _, Binding.CaseNew expected when expected <> Revision.zero ->
            Some(Refusal.revisionConflict Revision.zero)
        | _ -> Some FamilyRules.unavailable

    let available = Declarations.availability declaration blocked

    { available with
        Applicable = available.Applicable && state.IsNone
        Editable = editableInputs environment.CaseReferencePolicy
    }

let private gates
    (context: TaskContext)
    (environment: FamilyEnvironment)
    (state: DecisionFacts option)
    expected
    syntaxValid
    =
    let decision =
        { context.Decision with
            Access = environment.Requester
            Maintenance = environment.Maintenance
        }

    (if decision.Principal = environment.RequesterPrincipal then
         FamilyRules.permission environment Scope.Installation capability
     else
         Error FamilyRules.unavailable)
    |> Result.bind (fun () -> DecisionContext.lifetime decision context.Stamp.OperationId)
    |> Parsing.reject
    |> Result.bind (fun () ->
        if syntaxValid && state.IsSome then
            Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
        else
            DecisionContext.maintenance decision |> Parsing.reject)
    |> Result.bind (fun () ->
        if syntaxValid && expected <> Revision.zero then
            Error(Rejection.Refused(Refusal.revisionConflict Revision.zero))
        else
            Ok())

let private opening context reference registration parties =
    {
        Context = context
        ReferenceIntent = reference
        Registration = registration
        Parties = parties
    }

let private combine facts rows =
    match facts, rows with
    | Ok(reference, registration), Ok parties -> Ok(reference, registration, parties)
    | Error(Rejection.Input first), Error(Rejection.Input last) ->
        let issues = TaskInputIssues.toList first @ TaskInputIssues.toList last
        Error(Rejection.Input(TaskInputIssues.ofList issues |> Option.get))
    | Error error, _
    | _, Error error -> Error error

let admit (context: TaskContext) (environment: FamilyEnvironment) state binding inputs =
    match binding, context.Decision.BusinessDate with
    | Binding.CaseNew expected, Some today ->
        gates context environment state expected (List.isEmpty (check inputs))
        |> Result.bind (fun () ->
            combine
                (facts
                    environment.CaseReferencePolicy
                    (CaseAdmission.fieldAdmission today None)
                    inputs
                 |> Parsing.reject)
                (PartyTasks.parseRows
                    (context.Decision.Lifetime = DecisionLifetime.Recorded)
                    inputs))
        |> Result.map (fun (reference, registration, parties) ->
            opening context reference registration parties)
    | _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let preview (opening: AdmittedOpening) =
    {
        OpeningReview.Case =
            CaseDescription.opening (OpeningIntent.requested opening.Intent) opening.Registration
        Parties = opening.Parties |> List.map (snd >> PartDescriptions.openingParty)
    }

let private changes (context: TaskContext) reference registration parties =
    let id = CaseId.ofOperationId context.Stamp.OperationId

    [
        { FamilyRules.change
              context
              id
              (CaseId.value id)
              Revision.first
              (PartValue.Case
                  {
                      Disposition = None
                      ReopenReason = None
                      Reference = Some reference
                      Lifecycle = Lifecycle.Active
                      Handling = Handling.Open
                      Content =
                          registration
                          |> Option.map (fun registration ->
                              {
                                  Registration = registration
                                  Finance = FinancialPosition.undecided
                              })
                  }) with
            NaturalKey = Some(CaseReference.format reference)
        }
        yield!
            parties
            |> List.map (fun (ordinal, party) ->
                PartyTasks.initial
                    context
                    id
                    (PartyId.derive context.Stamp.OperationId ordinal)
                    party)
    ]
    |> TaskContext.changes context

let private issuance intent reference owner =
    let matches =
        match intent with
        | OpeningIntent.Automatic ->
            CaseReference.isGenerated reference
            && reference = CaseReference.canonical reference
        | OpeningIntent.Custom requested -> requested = reference

    if not matches then
        Error(Rejection.InvalidState RecordViolation.ReferenceMismatch)
    elif Option.isSome owner then
        Error(Rejection.Refused(Refusal.Case CaseRefusal.ReferenceUnavailable))
    else
        Ok()

let accept candidate owner (opening: AdmittedOpening) =
    issuance opening.Intent candidate owner
    |> Result.map (fun () ->
        changes
            opening.Context
            candidate
            (Some opening.Registration)
            (opening.Parties |> List.map (fun (ordinal, party) -> ordinal, Some party)))

let private referenceEvidence (context: TaskContext) (accepted: PartChange list) =
    let id = CaseId.ofOperationId context.Stamp.OperationId

    match accepted with
    | first :: rest when
        first.Aggregate = AggregateReference.Family id
        && first.PartId = CaseId.value id
        && first.Revision = Revision.first
        && first.Stamp = context.Stamp
        && not (rest |> List.exists (fun change -> change.Kind = PartKind.Case))
        ->
        match first.Value with
        | PartValue.Case { Reference = Some reference } when
            first.NaturalKey = Some(CaseReference.format reference)
            ->
            Ok reference
        | _ -> Error(Rejection.InvalidState RecordViolation.ReferenceMismatch)
    | _ -> Error(Rejection.InvalidState RecordViolation.InvalidHistoryStart)

let replay context (environment: FamilyEnvironment) state binding inputs retainedOnly accepted =
    if retainedOnly then
        match binding with
        | Binding.CaseNew expected ->
            gates context environment state expected true
            |> Result.bind (fun () ->
                intent environment.CaseReferencePolicy inputs |> Parsing.reject)
            |> Result.bind (fun intent ->
                referenceEvidence context accepted
                |> Result.bind (fun candidate ->
                    issuance intent candidate (environment.ReferenceOwner candidate)
                    |> Result.map (fun () ->
                        changes
                            context
                            candidate
                            None
                            (PartyTasks.rows inputs |> List.map (fun row -> row.Ordinal, None)))))
        | _ -> Error(Rejection.Refused FamilyRules.unavailable)
    else
        admit context environment state binding inputs
        |> Result.bind (fun opening ->
            referenceEvidence context accepted
            |> Result.bind (fun candidate ->
                accept candidate (environment.ReferenceOwner candidate) opening))

let task =
    {
        OpeningTask.Declaration = declaration
        Authorize =
            fun principal access _ _ ->
                FamilyRules.permissionFor principal access Scope.Installation capability
        Check = check
        Availability = availability
        EditableInputs = editableInputs
        Admit = admit
        Preview = preview
        Accept = accept
        Replay = replay
    }
