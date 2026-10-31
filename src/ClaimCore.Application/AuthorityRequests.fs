namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Conversion preserves the act stage and the record format's independent ownership.
module AuthorityRequests =
    let canonical (request: AuthorityRequestRecord) =
        let grant (value: RecordedGrant) =
            let scope =
                match value.Scope with
                | RecordedGrantScope.Installation -> RecordedGrantScope.Installation
                | RecordedGrantScope.Case(id, reference) ->
                    RecordedGrantScope.Case(id, CaseReference.canonical reference)

            { value with Scope = scope }

        { request with
            Act = AuthorityAct.map grant request.Act
        }

    let display (value: RecordedGrant) : ScopedGrant =
        {
            Role = value.Role
            Scope =
                match value.Scope with
                | RecordedGrantScope.Installation -> GrantScope.Installation
                | RecordedGrantScope.Case(id, reference) -> GrantScope.Case(id, reference)
        }

    let private authored (act: ValidAuthorityAct) : AuthoredAuthorityAct =
        AuthorityAct.map
            (fun (grant: AuthoredGrant) ->
                {
                    AuthoredGrant.Role = grant.Role
                    Scope = grant.Scope
                })
            act

    let toRecord (snapshot: CaseSnapshot option) command =
        let resolve =
            function
            | GrantTarget.Installation -> Ok RecordedGrantScope.Installation
            | GrantTarget.CaseReference requested ->
                match snapshot with
                | Some current when
                    CaseReference.key requested = CaseReference.identity current.Reference
                    ->
                    Ok(RecordedGrantScope.Case(current.CaseId, current.Reference))
                | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | GrantTarget.CaseId requested ->
                match snapshot with
                | Some current when requested = current.CaseId ->
                    Ok(RecordedGrantScope.Case(current.CaseId, current.Reference))
                | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

        AuthorityRecord.ofCommand resolve command

    /// Owner provisioning has a dedicated host boundary, never an ordinary wire request.
    let input operationId target act : Result<AuthorityRequest, Refusal> =
        match act with
        | AuthorityAct.ProvisionOwner -> Error(Refusal.Access AccessRefusal.ActNotPermitted)
        | act ->
            Ok
                {
                    OperationId = operationId
                    Target = PrincipalKey.toInput target
                    Act = authored act
                }

    let toInput (command: AuthorityCommand) =
        input (OperationId.value command.OperationId) command.Target command.Act

    /// Permanent parsing does not reapply current capability decisions.
    let fromRecord request = AuthorityRecord.interpret request

    let internal validRecord request =
        fromRecord request
        |> Result.exists (fun command ->
            command.Act <> AuthorityAct.ProvisionOwner
            || PrincipalKey.isHuman command.Target)

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
