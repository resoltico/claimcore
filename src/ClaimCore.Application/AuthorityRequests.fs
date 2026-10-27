namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Shared conversion between the primitive record codec and admitted application identities.
module AuthorityRequests =
    type private SharedAuthorityAct = ClaimCore.Domain.AuthorityAct

    /// Only a case-scope reference has alternate identity spellings; principals stay exact.
    let canonical (request: AuthorityRequestRecord) =
        let grant (value: GrantChange) =
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

        let act =
            match request.Act with
            | RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant value) ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant(grant value))
            | RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke value) ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke(grant value))
            | act -> act

        { request with Act = act }

    let private authoredAct =
        let grant role scope =
            {
                Role = Role.token role
                Scope = scope
            }

        function
        | AuthorityCommandAct.Register -> Some SharedAuthorityAct.Register
        | AuthorityCommandAct.Grant(role, scope) -> Some(SharedAuthorityAct.Grant(grant role scope))
        | AuthorityCommandAct.Revoke(role, scope) ->
            Some(SharedAuthorityAct.Revoke(grant role scope))
        | AuthorityCommandAct.Enable -> Some SharedAuthorityAct.Enable
        | AuthorityCommandAct.Disable -> Some SharedAuthorityAct.Disable
        | AuthorityCommandAct.ProvisionOwner -> None

    let toRecord (command: AuthorityCommand) : AuthorityRequestRecord =
        {
            OperationId = command.OperationId
            Target = PrincipalKey.describe command.Target
            Act =
                authoredAct command.Act
                |> Option.map RecordedAuthorityAct.Operation
                |> Option.defaultValue RecordedAuthorityAct.ProvisionOwner
        }

    /// Builds an authored input without admitting it; owner provisioning has no wire request.
    let input operationId target act : Result<ClaimCore.Domain.AuthorityRequest, Refusal> =
        authoredAct act
        |> Option.map (fun act ->
            ({
                OperationId = operationId
                Target = PrincipalKey.describe target
                Act = act
            }
            : ClaimCore.Domain.AuthorityRequest))
        |> function
            | Some request -> Ok request
            | None -> Error Refusal.ActNotPermitted

    let toInput (command: AuthorityCommand) =
        input command.OperationId command.Target command.Act

    /// Permanent interpretation performs structural parsing, never current grant/capability decisions.
    let fromRecord (request: AuthorityRequestRecord) =
        match request.Act with
        | RecordedAuthorityAct.Operation act ->
            Authority.parse
                {
                    OperationId = request.OperationId
                    Target = request.Target
                    Act = act
                }
        | RecordedAuthorityAct.ProvisionOwner ->
            PrincipalKey.fromStorage
                request.Target.Kind
                request.Target.Issuer
                request.Target.Subject
            |> Result.bind (Authority.provision request.OperationId)

    let internal validRecord (request: AuthorityRequestRecord) =
        match request.Act with
        | RecordedAuthorityAct.ProvisionOwner ->
            fromRecord request
            |> Result.exists (fun command -> PrincipalKey.isHuman command.Target)
        | RecordedAuthorityAct.Operation _ -> fromRecord request |> Result.isOk

    let internal targetCase (command: AuthorityCommand) =
        match command.Act with
        | AuthorityCommandAct.Grant(_, GrantTarget.CaseReference reference)
        | AuthorityCommandAct.Revoke(_, GrantTarget.CaseReference reference) -> Some reference
        | _ -> None
