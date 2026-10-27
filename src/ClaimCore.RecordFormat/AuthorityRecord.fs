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
        Codec.taggedOn
            DurableNames.Kind
            "RecordedAuthorityAct"
            ((RecordCodecs.authorityVariants Codec.text)
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
            || not (List.contains request.Target.Kind DurableNames.principalKinds)
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
        let principal =
            if request.Target.Kind = DurableNames.principalKind PrincipalKind.Human then
                PrincipalKey.human request.Target.Issuer request.Target.Subject
            elif request.Target.Kind = DurableNames.principalKind PrincipalKind.Service then
                PrincipalKey.service request.Target.Issuer request.Target.Subject
            else
                Error(Refusal.InvalidInput(InputTarget.PrincipalSubject, Violation.PrincipalFormat))

        let grant (value: GrantChange) =
            DurableNames.tryRole value.Role
            |> Option.map (fun role ->
                Ok
                    {
                        AuthorityGrant.Role = role
                        Scope = value.Scope
                    })
            |> Option.defaultValue (
                Error(Refusal.InvalidInput(InputTarget.Role, Violation.RoleFormat))
            )

        principal
        |> Result.bind (fun principal ->
            AuthorityAct.traverse grant request.Act
            |> Result.bind (Authority.restore request.OperationId principal))

    let ofCommand (command: AuthorityCommand) : AuthorityRequestRecord =
        let _, issuer, subject = PrincipalKey.storageParts command.Target

        let kind =
            if PrincipalKey.isHuman command.Target then
                PrincipalKind.Human
            else
                PrincipalKind.Service

        {
            OperationId = command.OperationId
            Target =
                {
                    Kind = DurableNames.principalKind kind
                    Issuer = issuer
                    Subject = subject
                }
            Act =
                AuthorityAct.map
                    (fun (grant: AuthorityGrant) ->
                        {
                            GrantChange.Role = DurableNames.role grant.Role
                            Scope = grant.Scope
                        })
                    command.Act
        }
