namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authored requests expand to separate durable effects; names never enter identity or grant payloads.
module PeopleRequests =
    let canonical (record: PeopleEffectRecord) =
        let grant (value: RecordedGrant) =
            let scope =
                match value.Scope with
                | RecordedGrantScope.Installation -> RecordedGrantScope.Installation
                | RecordedGrantScope.Case(id, reference) ->
                    RecordedGrantScope.Case(id, CaseReference.canonical reference)

            { value with Scope = scope }

        { record with
            Effect = PeopleEffect.map grant record.Effect
        }

    let display (value: RecordedGrant) : ScopedGrant =
        {
            Role = value.Role
            Scope =
                (match value.Scope with
                 | RecordedGrantScope.Installation -> GrantScope.Installation
                 | RecordedGrantScope.Case(id, reference) -> GrantScope.Case(id, reference))
        }

    let toRecords (snapshots: Map<GrantTarget, CaseSnapshot>) target (command: PeopleCommand) =
        let resolve target =
            match target with
            | GrantTarget.Installation -> Ok RecordedGrantScope.Installation
            | _ ->
                match Map.tryFind target snapshots with
                | Some current when
                    (match target with
                     | GrantTarget.CaseReference reference ->
                         CaseReference.key reference = CaseReference.identity current.Reference
                     | GrantTarget.CaseId id -> id = current.CaseId
                     | GrantTarget.Installation -> false)
                    ->
                    Ok(RecordedGrantScope.Case(current.CaseId, current.Reference))
                | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

        let grant (value: AuthoredGrant) =
            resolve value.Scope
            |> Result.map (fun scope ->
                {
                    RecordedGrant.Role = value.Role
                    Scope = scope
                })

        let effects =
            match command.Act with
            | PeopleAct.Admit admission ->
                admission.Grants
                |> List.fold
                    (fun state value ->
                        state
                        |> Result.bind (fun effects ->
                            grant value
                            |> Result.map (fun value -> PeopleEffect.Grant value :: effects)))
                    (Ok [])
                |> Result.map (fun effects ->
                    PeopleEffect.Register(PrincipalKey.toInput command.Target)
                    :: PeopleEffect.SetProfile admission.Profile
                    :: List.rev effects)
            | PeopleAct.Register profile ->
                Ok
                    [
                        PeopleEffect.Register(PrincipalKey.toInput command.Target)
                        PeopleEffect.SetProfile profile
                    ]
            | PeopleAct.ProvisionOwner(profile, roles) ->
                Ok
                    [
                        PeopleEffect.ProvisionOwner(PrincipalKey.toInput command.Target)
                        PeopleEffect.SetProfile profile
                        for role in roles do
                            PeopleEffect.Grant
                                {
                                    Role = role
                                    Scope = RecordedGrantScope.Installation
                                }
                    ]
            | PeopleAct.SetProfile profile -> Ok [ PeopleEffect.SetProfile profile ]
            | PeopleAct.EraseProfile -> Ok [ PeopleEffect.EraseProfile ]
            | PeopleAct.Grant value ->
                grant value |> Result.map (PeopleEffect.Grant >> List.singleton)
            | PeopleAct.Revoke value ->
                grant value |> Result.map (PeopleEffect.Revoke >> List.singleton)
            | PeopleAct.Enable -> Ok [ PeopleEffect.Enable ]
            | PeopleAct.Disable -> Ok [ PeopleEffect.Disable ]

        effects
        |> Result.map (
            List.map (fun effect ->
                {
                    PeopleEffectRecord.OperationId = OperationId.value command.OperationId
                    TargetActorId = target
                    Effect = effect
                })
        )

    /// Owner provisioning has a dedicated private host boundary, never an ordinary wire request.
    let input operationId target (act: ValidPeopleAct) : Result<PeopleRequest, Refusal> =
        match act with
        | PeopleAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | _ ->
            Ok
                {
                    OperationId = operationId
                    Target = PrincipalKey.toInput target
                    Act = PeopleAct.map id ActorProfile.authored act
                }

    let toInput (command: PeopleCommand) =
        input (OperationId.value command.OperationId) command.Target command.Act

    let internal targetCases (command: PeopleCommand) =
        let targets =
            match command.Act with
            | PeopleAct.Grant value
            | PeopleAct.Revoke value -> [ value.Scope ]
            | PeopleAct.Admit value -> value.Grants |> List.map _.Scope
            | _ -> []

        targets
        |> List.filter (function
            | GrantTarget.Installation -> false
            | _ -> true)
