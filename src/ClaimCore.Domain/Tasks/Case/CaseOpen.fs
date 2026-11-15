module ClaimCore.Domain.Tasks.Case.Open

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.open"
        ActSubject.Case
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.Register ]
        (operation
         :: required (field BusinessField.CaseReference)
         :: revision
         :: group TaskInputMode.Required RecordGroup.Registration)

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
    Parsing.check
        (fun inputs -> parse (fun _ -> Ok) (reference inputs) (CaseParsing.values inputs))
        inputs

let private gate (state: CaseFamily option) =
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
    (state: CaseFamily option)
    binding
    inputs
    =
    let fields = CaseParsing.values inputs
    let reference = reference inputs
    let syntax = parse (fun _ -> Ok) reference fields

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
                (if Result.isError syntax then None else gate state)
                |> Option.map Error
                |> Option.defaultValue (Ok()))
        |> Result.bind (fun () ->
            if Result.isError syntax || expected = Revision.zero then
                Ok()
            else
                Error(Refusal.revisionConflict Revision.zero))
        |> Result.bind (fun () ->
            let parsed =
                parse
                    (if decision.Lifetime = DecisionLifetime.Recorded then
                         fun _ -> Ok
                     else
                         CaseAdmission.fieldAdmission today None)
                    reference
                    fields

            Admission.map2
                (fun command () -> command)
                parsed
                (CaseParsing.chronology None true false fields)
            |> CaseInput.ordered)
        |> Result.map (fun command ->
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
            ])
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.family
        }
