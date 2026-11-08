namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authored requests expand to separate durable effects; names never enter identity or grant payloads.
module AuthorityRequests =
    let canonical (record: AuthorityEffectRecord) =
        let grant (value: RecordedGrant) =
            let scope =
                match value.Scope with
                | RecordedGrantScope.Installation -> RecordedGrantScope.Installation
                | RecordedGrantScope.Case(id, reference) ->
                    RecordedGrantScope.Case(id, CaseReference.canonical reference)

            { value with Scope = scope }

        { record with
            Effect = AuthorityEffect.map grant record.Effect
        }

    let display (value: RecordedGrant) : ScopedGrant =
        {
            Role = value.Role
            Scope =
                (match value.Scope with
                 | RecordedGrantScope.Installation -> GrantScope.Installation
                 | RecordedGrantScope.Case(id, reference) -> GrantScope.Case(id, reference))
        }

    let toRecords (snapshots: Map<GrantTarget, CaseSnapshot>) target (command: AuthorityCommand) =
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
            | AuthorityAct.Admit admission ->
                admission.Grants
                |> List.fold
                    (fun state value ->
                        state
                        |> Result.bind (fun effects ->
                            grant value
                            |> Result.map (fun value -> AuthorityEffect.Grant value :: effects)))
                    (Ok [])
                |> Result.map (fun effects ->
                    AuthorityEffect.Register(PrincipalKey.toInput command.Target)
                    :: AuthorityEffect.SetProfile admission.Profile
                    :: List.rev effects)
            | AuthorityAct.Register profile ->
                Ok
                    [
                        AuthorityEffect.Register(PrincipalKey.toInput command.Target)
                        AuthorityEffect.SetProfile profile
                    ]
            | AuthorityAct.ProvisionOwner(profile, roles) ->
                Ok
                    [
                        AuthorityEffect.ProvisionOwner(PrincipalKey.toInput command.Target)
                        AuthorityEffect.SetProfile profile
                        for role in roles do
                            AuthorityEffect.Grant
                                {
                                    Role = role
                                    Scope = RecordedGrantScope.Installation
                                }
                    ]
            | AuthorityAct.SetProfile profile -> Ok [ AuthorityEffect.SetProfile profile ]
            | AuthorityAct.EraseProfile -> Ok [ AuthorityEffect.EraseProfile ]
            | AuthorityAct.Grant value ->
                grant value |> Result.map (AuthorityEffect.Grant >> List.singleton)
            | AuthorityAct.Revoke value ->
                grant value |> Result.map (AuthorityEffect.Revoke >> List.singleton)
            | AuthorityAct.Enable -> Ok [ AuthorityEffect.Enable ]
            | AuthorityAct.Disable -> Ok [ AuthorityEffect.Disable ]

        effects
        |> Result.map (
            List.map (fun effect ->
                {
                    AuthorityEffectRecord.OperationId = OperationId.value command.OperationId
                    TargetActorId = target
                    Effect = effect
                })
        )

    /// Owner provisioning has a dedicated private host boundary, never an ordinary wire request.
    let input operationId target (act: ValidAuthorityAct) : Result<AuthorityRequest, Refusal> =
        match act with
        | AuthorityAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | _ ->
            Ok
                {
                    OperationId = operationId
                    Target = PrincipalKey.toInput target
                    Act = AuthorityAct.map id ActorProfile.authored act
                }

    let toInput (command: AuthorityCommand) =
        input (OperationId.value command.OperationId) command.Target command.Act

    let internal targetCases (command: AuthorityCommand) =
        let targets =
            match command.Act with
            | AuthorityAct.Grant value
            | AuthorityAct.Revoke value -> [ value.Scope ]
            | AuthorityAct.Admit value -> value.Grants |> List.map _.Scope
            | _ -> []

        targets
        |> List.filter (function
            | GrantTarget.Installation -> false
            | _ -> true)
