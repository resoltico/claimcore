namespace ClaimCore.RecordFormat

open ClaimCore.Contract

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type RecordedAuthorityKind =
    | ProvisionOwner
    | RegisterActor
    | SetGrant
    | SetEnabled

[<RequireQualifiedAccess>]
type RecordedAuthorityAct =
    | RegisterActor
    | ProvisionOwner
    | SetGrant of role: string * scope: GrantTarget * active: bool
    | SetEnabled of enabled: bool

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: Principal
        Act: RecordedAuthorityAct
    }

module RecordedAuthorityActs =
    let kind =
        function
        | RecordedAuthorityAct.ProvisionOwner -> RecordedAuthorityKind.ProvisionOwner
        | RecordedAuthorityAct.RegisterActor -> RecordedAuthorityKind.RegisterActor
        | RecordedAuthorityAct.SetGrant _ -> RecordedAuthorityKind.SetGrant
        | RecordedAuthorityAct.SetEnabled _ -> RecordedAuthorityKind.SetEnabled

    let private tokens =
        [
            RecordedAuthorityKind.ProvisionOwner,
            (AuthorityKinds.token AuthorityKind.ProvisionOwner)
            RecordedAuthorityKind.RegisterActor, (AuthorityKinds.token AuthorityKind.RegisterActor)
            RecordedAuthorityKind.SetGrant, (AuthorityKinds.token AuthorityKind.SetGrant)
            RecordedAuthorityKind.SetEnabled, (AuthorityKinds.token AuthorityKind.SetEnabled)
        ]

    let all = tokens |> List.map fst

    let token kind =
        tokens |> List.find (fst >> (=) kind) |> snd

    let tryKind value =
        tokens |> List.tryFind (snd >> (=) value) |> Option.map fst

module AuthorityRecord =
    let private scope root =
        let path = "$.act.scope"

        match Json.text path "kind" root with
        | "INSTALLATION" ->
            Json.properties path [ "kind" ] root
            GrantTarget.Installation
        | "CASE" ->
            Json.properties path [ "kind"; "caseReference" ] root
            let reference = Json.text path "caseReference" root

            match CaseReference.validate reference with
            | Ok() -> GrantTarget.CaseReference reference
            | Error _ -> Json.reject (path + ".caseReference") "Invalid case reference."
        | _ -> Json.reject (path + ".kind") "Unknown grant scope."

    let private act root =
        let path = "$.act"

        match Json.text path "kind" root with
        | value when value = (AuthorityKinds.token AuthorityKind.ProvisionOwner) ->
            Json.properties path [ "kind" ] root
            RecordedAuthorityAct.ProvisionOwner
        | value when value = (AuthorityKinds.token AuthorityKind.RegisterActor) ->
            Json.properties path [ "kind" ] root
            RecordedAuthorityAct.RegisterActor
        | value when value = (AuthorityKinds.token AuthorityKind.SetGrant) ->
            Json.properties path [ "kind"; "role"; "scope"; "active" ] root

            RecordedAuthorityAct.SetGrant(
                Json.text path "role" root,
                Json.required path "scope" root |> scope,
                Json.boolean path "active" root
            )
        | value when value = (AuthorityKinds.token AuthorityKind.SetEnabled) ->
            Json.properties path [ "kind"; "enabled" ] root
            RecordedAuthorityAct.SetEnabled(Json.boolean path "enabled" root)
        | _ -> Json.reject (path + ".kind") "Unknown authority act."

    let private read root =
        let members = [ "operationId"; "target"; "act" ]
        Json.properties "$" ("format" :: members) root
        let target = Json.required "$" "target" root
        Json.properties "$.target" [ "kind"; "issuer"; "subject" ] target
        let kind = Json.text "$.target" "kind" target

        if kind <> PrincipalKinds.Human && kind <> PrincipalKinds.Service then
            Json.reject "$.target.kind" "Unknown principal kind."

        {
            OperationId = Json.guid "$" "operationId" root
            Target =
                {
                    Kind = kind
                    Issuer = Json.text "$.target" "issuer" target
                    Subject = Json.text "$.target" "subject" target
                }
            Act = Json.required "$" "act" root |> act
        }

    let decode maxBytes bytes =
        Json.bounded maxBytes bytes (Json.dispatch [ 2L, read ])

    let private scopeValue =
        function
        | GrantTarget.Installation -> JsonValue.Object [ "kind", JsonValue.String "INSTALLATION" ]
        | GrantTarget.CaseReference reference ->
            match CaseReference.validate reference with
            | Error _ -> invalidArg (nameof reference) "Invalid case reference."
            | Ok() ->
                JsonValue.Object
                    [ "kind", JsonValue.String "CASE"; "caseReference", JsonValue.String reference ]

    let private actValue act =
        let members =
            match act with
            | RecordedAuthorityAct.ProvisionOwner
            | RecordedAuthorityAct.RegisterActor -> []
            | RecordedAuthorityAct.SetGrant(role, scope, active) ->
                [
                    "role", JsonValue.String(Json.persistedString "$.act.role" role)
                    "scope", scopeValue scope
                    "active", JsonValue.Boolean active
                ]
            | RecordedAuthorityAct.SetEnabled enabled -> [ "enabled", JsonValue.Boolean enabled ]

        let kind = RecordedAuthorityActs.kind act |> RecordedAuthorityActs.token
        JsonValue.Object(("kind", JsonValue.String kind) :: members)

    let private value (request: AuthorityRequestRecord) =
        if request.OperationId = Guid.Empty then
            invalidArg (nameof request) "Use a nonempty operation ID."

        if
            request.Target.Kind <> PrincipalKinds.Human
            && request.Target.Kind <> PrincipalKinds.Service
        then
            invalidArg (nameof request) "Unknown principal kind."

        let target =
            JsonValue.Object
                [
                    "kind",
                    JsonValue.String(Json.persistedString "$.target.kind" request.Target.Kind)
                    "issuer",
                    JsonValue.String(Json.persistedString "$.target.issuer" request.Target.Issuer)
                    "subject",
                    JsonValue.String(Json.persistedString "$.target.subject" request.Target.Subject)
                ]

        let members =
            [
                "operationId", JsonValue.String(request.OperationId.ToString("D"))
                "target", target
                "act", actValue request.Act
            ]

        JsonValue.Object(("format", JsonValue.Integer(int64 RecordVersions.Authority)) :: members)

    let encode request = value request |> CanonicalWriter.bytes
