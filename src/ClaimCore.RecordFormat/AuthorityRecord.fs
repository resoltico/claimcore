namespace ClaimCore.RecordFormat

open System
open ClaimCore.Json
open ClaimCore.Domain

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthoredAuthorityAct
    }

module AuthorityRecord =
    let private act =
        Codec.tagged
            "RecordedAuthorityAct"
            ((RecordCodecs.authorityVariants Codec.text)
             @ [ Codec.unitVariant "PROVISION_OWNER" AuthorityAct.ProvisionOwner ])

    let private fields =
        let version = int64 RecordVersions.Authority

        Codec.record (fun operationId target act () ->
            {
                OperationId = operationId
                Target = target
                Act = act
            })
        |> Codec.field "operationId" _.OperationId Codec.uuid
        |> Codec.field "target" _.Target RecordCodecs.principal
        |> Codec.field "act" _.Act act
        |> Codec.field
            "format"
            (fun _ -> ())
            (Codec.integer version version |> Codec.map (fun _ -> ()) (fun () -> version))

    let private codec =
        Codec.tagged "AuthorityRequestRecord.Subject" [ Codec.variant "AUTHORITY" id Some fields ]
        |> Codec.named "AuthorityRequestRecord"

    let private validScope =
        function
        | AuthorityAct.Grant {
                                 Scope = GrantTarget.CaseReference reference
                             }
        | AuthorityAct.Revoke {
                                  Scope = GrantTarget.CaseReference reference
                              } ->
            (CaseReference.parse
             >> Result.map ignore
             >> Result.mapError (fun violation ->
                 Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation)))
                reference
            |> Result.isOk
        | _ -> true

    let private validate request =
        if
            request.OperationId = Guid.Empty
            || not (List.contains request.Target.Kind PrincipalKind.all)
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
