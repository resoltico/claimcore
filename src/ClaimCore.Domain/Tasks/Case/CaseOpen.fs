module ClaimCore.Domain.Tasks.Case.Open

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations
open ClaimCore.Domain.Tasks.Party

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
         :: (required (field BusinessField.CaseReference) |> retained)
         :: revision
         :: group TaskInputMode.Optional RecordGroup.Registration)
        [ PartyTasks.collection ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command =
    {
        Reference: CaseReference
        Registration: Registration
    }

let private reference inputs =
    Parsing.scalar (InputTarget.Field BusinessField.CaseReference) CaseReference.parse inputs

let private parse admit reference fields =
    Admission.map2
        (fun reference registration ->
            {
                Reference = reference
                Registration = registration
            })
        reference
        (CaseParsing.registration admit fields)

let check inputs =
    let facts =
        Parsing.check
            (fun inputs -> parse (fun _ -> Ok) (reference inputs) (CaseParsing.values inputs))
            inputs

    match PartyTasks.parseRows true inputs with
    | Error(Rejection.Input issues) -> facts @ TaskInputIssues.toList issues
    | _ -> facts

let private gate (state: DecisionFacts option) =
    match state with
    | Some state when state.Case.Value.Lifecycle = Lifecycle.Erased ->
        Some(FamilyRules.notOffered state)
    | Some _ -> Some(Refusal.Case CaseRefusal.CaseAlreadyExists)
    | None -> None

let availability environment state binding =
    let admission = FamilyRules.permission environment Scope.Installation capability

    let blocked =
        match admission, binding with
        | Error refusal, _ -> Some refusal
        | _, Binding.CaseNew expected when expected = Revision.zero ->
            gate state
            |> Option.orElse (
                if environment.Maintenance then
                    Some Refusal.RegisterMaintenance
                else
                    None
            )
        | _ -> Some(Refusal.revisionConflict Revision.zero)

    Declarations.availability declaration blocked

let decide
    (context: TaskContext)
    (environment: FamilyEnvironment)
    (state: DecisionFacts option)
    binding
    inputs
    =
    let fields = CaseParsing.values inputs
    let reference = reference inputs
    let syntax = check inputs

    match binding, context.Decision.BusinessDate with
    | Binding.CaseNew expected, Some today ->
        let decision =
            { context.Decision with
                Access = environment.Requester
                Maintenance = environment.Maintenance
            }

        DecisionContext.admit
            decision
            context.Stamp.OperationId
            (if decision.Principal <> environment.RequesterPrincipal then
                 Error FamilyRules.unavailable
             else
                 FamilyRules.permission environment Scope.Installation capability)
            (fun () ->
                (if not syntax.IsEmpty then None else gate state)
                |> Option.map Error
                |> Option.defaultValue (Ok()))
        |> Result.bind (fun () ->
            if not syntax.IsEmpty || expected = Revision.zero then
                Ok()
            else
                Error(Refusal.revisionConflict Revision.zero))
        |> Result.bind (fun () -> parse (CaseAdmission.fieldAdmission today None) reference fields)
        |> Parsing.reject
        |> Result.bind (fun command ->
            PartyTasks.parseRows (context.Decision.Lifetime = DecisionLifetime.Recorded) inputs
            |> Result.map (fun parties -> command, parties))
        |> Result.map (fun (command, parties) ->
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
                              Reference = Some command.Reference
                              Lifecycle = Lifecycle.Active
                              Status = CaseStatus.Opened
                              Content =
                                  Some
                                      {
                                          Registration = command.Registration
                                          Finance = FinancialPosition.undecided
                                      }
                          }) with
                    NaturalKey = Some(CaseReference.format command.Reference)
                }
            ]
            @ (parties
               |> List.map (fun (ordinal, party) ->
                   PartyTasks.initial
                       context
                       id
                       (PartyId.derive context.Stamp.OperationId ordinal)
                       (Some party))))
        |> Result.map (TaskContext.changes context)
    | _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained (context: TaskContext) (environment: FamilyEnvironment) state binding inputs =
    match binding with
    | Binding.CaseNew expected when expected = Revision.zero ->
        DecisionContext.admit
            { context.Decision with
                Access = environment.Requester
                Maintenance = environment.Maintenance
            }
            context.Stamp.OperationId
            (FamilyRules.permission environment Scope.Installation capability)
            (fun () -> gate state |> Option.map Error |> Option.defaultValue (Ok()))
        |> Result.bind (fun () -> reference inputs)
        |> Result.map (fun reference ->
            let id = CaseId.ofOperationId context.Stamp.OperationId

            [
                { FamilyRules.change
                      context
                      id
                      (CaseId.value id)
                      Revision.first
                      (PartValue.Case
                          {
                              Reference = Some reference
                              Lifecycle = Lifecycle.Active
                              Status = CaseStatus.Opened
                              Content = None
                              Disposition = None
                          }) with
                    NaturalKey = Some(CaseReference.format reference)
                }
            ]
            @ (PartyTasks.rows inputs
               |> List.map (fun row ->
                   PartyTasks.initial
                       context
                       id
                       (PartyId.derive context.Stamp.OperationId row.Ordinal)
                       None)))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun principal access _ _ ->
                    FamilyRules.permissionFor principal access Scope.Installation capability
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
