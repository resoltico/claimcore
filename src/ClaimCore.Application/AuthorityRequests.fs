namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Conversion preserves the act stage and the record format's independent ownership.
module AuthorityRequests =
    let canonical (request: AuthorityRequestRecord) =
        let grant (value: AuthoredGrant) =
            let scope =
                match value.Scope with
                | GrantTarget.Installation -> GrantTarget.Installation
                | GrantTarget.CaseReference reference ->
                    GrantTarget.CaseReference(
                        if System.String.IsNullOrEmpty reference then
                            reference
                        else
                            CaseReference.key reference
                    )

            { value with Scope = scope }

        { request with
            Act = AuthorityAct.map grant request.Act
        }

    let private authored (act: ValidAuthorityAct) : AuthoredAuthorityAct =
        AuthorityAct.map
            (fun (grant: AuthoredGrant) ->
                {
                    AuthoredGrant.Role = grant.Role
                    Scope = grant.Scope
                })
            act

    let toRecord command = AuthorityRecord.ofCommand command

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
        input command.OperationId command.Target command.Act

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
                              } -> Some reference
        | _ -> None
