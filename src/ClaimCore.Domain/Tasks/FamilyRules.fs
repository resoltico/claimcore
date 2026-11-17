namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes

/// Shared family admission concerns have no task identity or per-task dispatch.
module internal FamilyRules =
    let unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

    let permission (environment: FamilyEnvironment) scope capability =
        if Access.can environment.RequesterPrincipal environment.Requester capability scope then
            Ok()
        elif
            scope <> Scope.Installation
            && Access.can
                environment.RequesterPrincipal
                environment.Requester
                Capability.ReadCase
                scope
        then
            Error(Refusal.Access(AccessRefusal.NotAuthorized capability))
        else
            Error unavailable

    let notOffered (state: CaseFamily) =
        match state.Case.Value.Lifecycle with
        | Lifecycle.Erased -> Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)
        | Lifecycle.Voided -> Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided)
        | Lifecycle.Active when state.Case.Value.Status = CaseStatus.Closed ->
            Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)
        | Lifecycle.Active ->
            match state.Case.Value.Content with
            | None -> unavailable
            | Some content ->
                CaseHeadline.ofState (
                    CaseState.Active(
                        state.Case.Value.Status,
                        CaseProgress.ofFinancial content.Finance
                    )
                )
                |> CaseRefusal.NotOffered
                |> Refusal.Case

    let active requireOpen (state: CaseFamily) =
        if
            state.Case.Value.Lifecycle <> Lifecycle.Active
            || (requireOpen && state.Case.Value.Status <> CaseStatus.Opened)
        then
            Some(notOffered state)
        else
            None

    let target (state: CaseFamily) binding =
        match Binding.caseTarget binding with
        | Some(CaseSelector.Id id) when id = state.Id -> Ok()
        | Some(CaseSelector.Reference reference) ->
            match CaseReference.parse reference with
            | Ok reference when
                CaseReference.identity reference = CaseReference.identity state.Reference
                ->
                Ok()
            | _ -> Error unavailable
        | _ -> Error unavailable

    let availability
        declaration
        (environment: FamilyEnvironment)
        (state: CaseFamily option)
        binding
        capability
        gate
        =
        let result =
            match state with
            | None -> Error unavailable
            | Some state ->
                permission environment (Scope.Case state.Id) capability
                |> Result.bind (fun () -> target state binding)
                |> Result.bind (fun () ->
                    gate state |> Option.map Error |> Option.defaultValue (Ok()))
                |> Result.bind (fun () ->
                    if environment.Maintenance then
                        Error Refusal.RegisterMaintenance
                    else
                        Ok())

        Declarations.availability
            declaration
            (match result with
             | Ok() -> None
             | Error refusal -> Some refusal)

    let admitPermission
        (context: TaskContext)
        (environment: FamilyEnvironment)
        (state: CaseFamily)
        binding
        permitted
        gate
        =
        let contextDecision =
            { context.Decision with
                Access = environment.Requester
                Maintenance = environment.Maintenance
            }

        DecisionContext.admit
            contextDecision
            context.Stamp.OperationId
            (if contextDecision.Principal <> environment.RequesterPrincipal then
                 Error unavailable
             else
                 permitted)
            (fun () ->
                target state binding
                |> Result.bind (fun () ->
                    gate state |> Option.map Error |> Option.defaultValue (Ok())))

    let admit context environment state binding capability gate =
        admitPermission
            context
            environment
            state
            binding
            (permission environment (Scope.Case state.Id) capability)
            gate

    let revision expected actual =
        if expected <> actual || Revision.value actual = Revision.Maximum then
            Error(Refusal.revisionConflict actual)
        else
            Ok()

    let change (context: TaskContext) state partId revision value =
        {
            Aggregate = AggregateReference.Family state
            PartId = partId
            Revision = revision
            NaturalKey = None
            Value = value
            Erases = None
            Stamp = context.Stamp
        }
