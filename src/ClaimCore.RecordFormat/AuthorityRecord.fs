namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain

/// Primitive format values; Application owns principal admission and the role matrix.
type RecordedPrincipal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

[<RequireQualifiedAccess>]
type RecordedGrantScope =
    | Installation
    | CaseReference of string

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
    | SetGrant of role: string * scope: RecordedGrantScope * active: bool
    | SetEnabled of enabled: bool

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: RecordedPrincipal
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
            RecordedAuthorityKind.ProvisionOwner, "PROVISION_OWNER"
            RecordedAuthorityKind.RegisterActor, "REGISTER_ACTOR"
            RecordedAuthorityKind.SetGrant, "SET_GRANT"
            RecordedAuthorityKind.SetEnabled, "SET_ENABLED"
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
            RecordedGrantScope.Installation
        | "CASE" ->
            Json.properties path [ "kind"; "caseReference" ] root
            let reference = Json.text path "caseReference" root

            match CaseReference.validate reference with
            | Ok() -> RecordedGrantScope.CaseReference reference
            | Error _ -> Json.reject (path + ".caseReference") "Invalid case reference."
        | _ -> Json.reject (path + ".kind") "Unknown grant scope."

    let private act root =
        let path = "$.act"

        match Json.text path "kind" root with
        | "PROVISION_OWNER" ->
            Json.properties path [ "kind" ] root
            RecordedAuthorityAct.ProvisionOwner
        | "REGISTER_ACTOR" ->
            Json.properties path [ "kind" ] root
            RecordedAuthorityAct.RegisterActor
        | "SET_GRANT" ->
            Json.properties path [ "kind"; "role"; "scope"; "active" ] root

            RecordedAuthorityAct.SetGrant(
                Json.text path "role" root,
                Json.required path "scope" root |> scope,
                Json.boolean path "active" root
            )
        | "SET_ENABLED" ->
            Json.properties path [ "kind"; "enabled" ] root
            RecordedAuthorityAct.SetEnabled(Json.boolean path "enabled" root)
        | _ -> Json.reject (path + ".kind") "Unknown authority act."

    let private read root =
        let members = [ "operationId"; "target"; "act" ]
        Json.properties "$" ("format" :: members) root
        let target = Json.required "$" "target" root
        Json.properties "$.target" [ "kind"; "issuer"; "subject" ] target
        let kind = Json.text "$.target" "kind" target

        if kind <> "HUMAN" && kind <> "SERVICE" then
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
        | RecordedGrantScope.Installation ->
            CanonicalValue.Object [ "kind", CanonicalValue.String "INSTALLATION" ]
        | RecordedGrantScope.CaseReference reference ->
            match CaseReference.validate reference with
            | Error _ -> invalidArg (nameof reference) "Invalid case reference."
            | Ok() ->
                CanonicalValue.Object
                    [
                        "kind", CanonicalValue.String "CASE"
                        "caseReference", CanonicalValue.String reference
                    ]

    let private actValue act =
        let members =
            match act with
            | RecordedAuthorityAct.ProvisionOwner
            | RecordedAuthorityAct.RegisterActor -> []
            | RecordedAuthorityAct.SetGrant(role, scope, active) ->
                [
                    "role", CanonicalValue.String(Json.persistedString "$.act.role" role)
                    "scope", scopeValue scope
                    "active", CanonicalValue.Boolean active
                ]
            | RecordedAuthorityAct.SetEnabled enabled ->
                [ "enabled", CanonicalValue.Boolean enabled ]

        let kind = RecordedAuthorityActs.kind act |> RecordedAuthorityActs.token
        CanonicalValue.Object(("kind", CanonicalValue.String kind) :: members)

    let private value (request: AuthorityRequestRecord) =
        if request.OperationId = Guid.Empty then
            invalidArg (nameof request) "Use a nonempty operation ID."

        if request.Target.Kind <> "HUMAN" && request.Target.Kind <> "SERVICE" then
            invalidArg (nameof request) "Unknown principal kind."

        let target =
            CanonicalValue.Object
                [
                    "kind",
                    CanonicalValue.String(Json.persistedString "$.target.kind" request.Target.Kind)
                    "issuer",
                    CanonicalValue.String(
                        Json.persistedString "$.target.issuer" request.Target.Issuer
                    )
                    "subject",
                    CanonicalValue.String(
                        Json.persistedString "$.target.subject" request.Target.Subject
                    )
                ]

        let members =
            [
                "operationId", CanonicalValue.String(request.OperationId.ToString("D"))
                "target", target
                "act", actValue request.Act
            ]

        CanonicalValue.Object(
            ("format", CanonicalValue.Integer(int64 RecordVersions.Authority)) :: members
        )

    let encode request = value request |> CanonicalJson.encode
