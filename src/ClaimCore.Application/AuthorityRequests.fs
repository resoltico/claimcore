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

    let toRecords (snapshot: CaseSnapshot option) target (command: AuthorityCommand) =
        let resolve =
            function
            | GrantTarget.Installation -> Ok RecordedGrantScope.Installation
            | GrantTarget.CaseReference reference ->
                match snapshot with
                | Some current when
                    CaseReference.key reference = CaseReference.identity current.Reference
                    ->
                    Ok(RecordedGrantScope.Case(current.CaseId, current.Reference))
                | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | GrantTarget.CaseId id ->
                match snapshot with
                | Some current when current.CaseId = id ->
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
            | AuthorityAct.Register profile ->
                Ok
                    [
                        AuthorityEffect.Register(PrincipalKey.toInput command.Target)
                        AuthorityEffect.SetProfile profile
                    ]
            | AuthorityAct.ProvisionOwner profile ->
                Ok
                    [
                        AuthorityEffect.ProvisionOwner(PrincipalKey.toInput command.Target)
                        AuthorityEffect.SetProfile profile
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

    let internal targetCase (command: AuthorityCommand) =
        match command.Act with
        | AuthorityAct.Grant {
                                 Scope = GrantTarget.CaseReference reference
                             }
        | AuthorityAct.Revoke {
                                  Scope = GrantTarget.CaseReference reference
                              } -> Some(CaseSelector.Reference reference)
        | AuthorityAct.Grant { Scope = GrantTarget.CaseId id }
        | AuthorityAct.Revoke { Scope = GrantTarget.CaseId id } -> Some(CaseSelector.Id id)
        | _ -> None
