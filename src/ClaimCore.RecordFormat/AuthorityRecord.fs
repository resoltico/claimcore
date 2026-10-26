namespace ClaimCore.RecordFormat

open System
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Domain

type private SharedAuthorityAct = ClaimCore.Contract.AuthorityAct

[<RequireQualifiedAccess>]
type RecordedAuthorityAct =
    | ProvisionOwner
    | Operation of ClaimCore.Contract.AuthorityAct

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: Principal
        Act: RecordedAuthorityAct
    }

module RecordedAuthorityActs =
    let kind =
        function
        | RecordedAuthorityAct.ProvisionOwner -> AuthorityKind.ProvisionOwner
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Register -> AuthorityKind.Register
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant _) -> AuthorityKind.Grant
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke _) -> AuthorityKind.Revoke
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Enable -> AuthorityKind.Enable
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Disable -> AuthorityKind.Disable

    let all = AuthorityKinds.all

    let token = AuthorityKinds.token

    let tryKind value =
        all |> List.tryFind (token >> (=) value)

module AuthorityRecord =
    let private owner =
        Codec.record ()
        |> ClaimCore.Contract.Protocol.ResponseScalars.fixedField
            "kind"
            (AuthorityKinds.token AuthorityKind.ProvisionOwner)
        |> Codec.build
        |> Codec.named "ProvisionOwnerRecordAct"

    let private act =
        Codec.choice
            "RecordedAuthorityAct"
            [
                Codec.branch
                    RecordedAuthorityAct.Operation
                    (function
                    | RecordedAuthorityAct.Operation value -> Some value
                    | _ -> None)
                    (ClaimCore.Contract.Protocol.RequestCodecs.authorityActWith Codec.text)
                Codec.branch
                    (fun () -> RecordedAuthorityAct.ProvisionOwner)
                    (function
                    | RecordedAuthorityAct.ProvisionOwner -> Some()
                    | _ -> None)
                    owner
            ]

    let private codec =
        Codec.record (fun operationId target act ->
            {
                OperationId = operationId
                Target = target
                Act = act
            })
        |> Codec.field "operationId" _.OperationId Codec.uuid
        |> Codec.field "target" _.Target ClaimCore.Contract.Protocol.RequestCodecs.principal
        |> Codec.field "act" _.Act act
        |> Codec.build
        |> Codec.withScope OperationScopes.Authority
        |> Codec.withFormat (int64 RecordVersions.Authority)
        |> Codec.named "AuthorityRequestRecord"

    let private validScope =
        function
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant {
                                                                      Scope = GrantTarget.CaseReference reference
                                                                  })
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke {
                                                                       Scope = GrantTarget.CaseReference reference
                                                                   }) ->
            CaseReference.validate reference |> Result.isOk
        | _ -> true

    let private validate request =
        if
            request.OperationId = Guid.Empty
            || not (List.contains request.Target.Kind PrincipalKinds.all)
            || not (validScope request.Act)
        then
            Error
                {
                    Field = "$"
                    Message = "Invalid authority record format."
                }
        else
            Ok request

    let decode maximumBytes bytes =
        Codec.decode maximumBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let encode request =
        match validate request with
        | Error _ -> invalidArg (nameof request) "Invalid authority record format."
        | Ok value -> codec.Write value |> CanonicalWriter.bytes
