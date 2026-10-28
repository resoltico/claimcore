namespace ClaimCore.RecordFormat

open System
open ClaimCore.Json
open ClaimCore.Domain

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: PrincipalInput
        Act: AuthoredAuthorityAct
    }

module AuthorityRecord =
    let private act =
        Codec.taggedOn
            DurableNames.Kind
            "RecordedAuthorityAct"
            ((RecordCodecs.authorityVariants RecordCodecs.role)
             @ [
                 Codec.unitVariant
                     (DurableNames.authorityAct AuthorityKind.ProvisionOwner)
                     AuthorityAct.ProvisionOwner
             ])

    let private fields =
        let version = int64 RecordVersions.Authority

        Codec.record (fun operationId target act () ->
            {
                OperationId = operationId
                Target = target
                Act = act
            })
        |> Codec.field DurableNames.OperationId _.OperationId Codec.uuid
        |> Codec.field DurableNames.Target _.Target RecordCodecs.principal
        |> Codec.field DurableNames.Act _.Act act
        |> Codec.field
            DurableNames.Format
            (fun _ -> ())
            (Codec.integer version version |> Codec.map (fun _ -> ()) (fun () -> version))

    let private codec =
        Codec.taggedOn
            DurableNames.Kind
            "AuthorityRequestRecord.Subject"
            [ Codec.variant DurableNames.AuthoritySubject id Some fields ]
        |> Codec.named "AuthorityRequestRecord"

    let private validScope =
        function
        | AuthorityAct.Grant {
                                 Scope = GrantTarget.CaseReference reference
                             }
        | AuthorityAct.Revoke {
                                  Scope = GrantTarget.CaseReference reference
                              } -> CaseReference.parse reference |> Result.isOk
        | _ -> true

    let private validate request =
        if request.OperationId = Guid.Empty || not (validScope request.Act) then
            Error
                {
                    Field = "$"
                    Message = "Invalid authority record format."
                }
        else
            Ok request

    let decode maximumBytes bytes =
        Codec.decode (min maximumBytes RecordLimits.MaximumRequestRecordBytes) codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let encode request =
        match validate request with
        | Error _ -> invalidArg (nameof request) "Invalid authority record format."
        | Ok value -> codec.Write value |> CanonicalWriter.bytes |> RecordLimits.requestBytes

    /// Permanent spellings are interpreted here; Domain receives typed principal and grant facts.
    let interpret (request: AuthorityRequestRecord) =
        PrincipalKey.ofKind request.Target.Kind request.Target.Issuer request.Target.Subject
        |> Result.bind (fun principal ->
            let act =
                AuthorityAct.map
                    (fun (value: AuthoredGrant) ->
                        {
                            AuthoredGrant.Role = value.Role
                            Scope = value.Scope
                        })
                    request.Act

            Authority.restore request.OperationId principal act)
        |> Result.mapError (fun _ ->
            {
                Field = "$"
                Message = "Invalid authority record values."
            })

    let ofCommand (command: AuthorityCommand) : AuthorityRequestRecord =
        let kind, issuer, subject = PrincipalKey.parts command.Target

        {
            OperationId = OperationId.value command.OperationId
            Target =
                {
                    Kind = kind
                    Issuer = issuer
                    Subject = subject
                }
            Act =
                AuthorityAct.map
                    (fun (grant: AuthoredGrant) ->
                        {
                            AuthoredGrant.Role = grant.Role
                            Scope = grant.Scope
                        })
                    command.Act
        }
