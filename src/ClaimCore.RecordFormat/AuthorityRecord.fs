namespace ClaimCore.RecordFormat

open System
open System.Text.Json

/// Format data only. Application admits principal kinds, identities, roles and grant scopes.
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
type RecordedAuthorityAction =
    | RegisterActor
    | ProvisionOwner
    | SetGrant of role: string * scope: RecordedGrantScope * active: bool
    | SetEnabled of enabled: bool

type AuthorityRequestRecord =
    {
        EventId: Guid
        Target: RecordedPrincipal
        Action: RecordedAuthorityAction
    }

/// Canonical authority payload within ledger entry format 1; it grants no authority by itself.
module AuthorityRecord =
    let private scope root =
        let path = "$.action.scope"

        match Json.text path "type" root with
        | "INSTALLATION" ->
            Json.properties path [ "type" ] root
            RecordedGrantScope.Installation
        | "CASE" ->
            Json.properties path [ "type"; "caseReference" ] root
            RecordedGrantScope.CaseReference(Json.text path "caseReference" root)
        | _ -> Json.reject (path + ".type") "Unknown grant scope shape."

    let private action root =
        let path = "$.action"

        match Json.text path "type" root with
        | "PROVISION_OWNER" ->
            Json.properties path [ "type" ] root
            RecordedAuthorityAction.ProvisionOwner
        | "REGISTER_ACTOR" ->
            Json.properties path [ "type" ] root
            RecordedAuthorityAction.RegisterActor
        | "SET_GRANT" ->
            Json.properties path [ "type"; "role"; "scope"; "active" ] root

            RecordedAuthorityAction.SetGrant(
                Json.text path "role" root,
                Json.required path "scope" root |> scope,
                Json.boolean path "active" root
            )
        | "SET_ENABLED" ->
            Json.properties path [ "type"; "enabled" ] root
            RecordedAuthorityAction.SetEnabled(Json.boolean path "enabled" root)
        | _ -> Json.reject (path + ".type") "Unknown authority action."

    let decode maxBytes bytes =
        Json.bounded maxBytes bytes (fun root ->
            Json.properties "$" [ "eventId"; "target"; "action" ] root
            let target = Json.required "$" "target" root
            Json.properties "$.target" [ "kind"; "issuer"; "subject" ] target

            {
                EventId = Json.guid "$" "eventId" root
                Target =
                    {
                        Kind = Json.text "$.target" "kind" target
                        Issuer = Json.text "$.target" "issuer" target
                        Subject = Json.text "$.target" "subject" target
                    }
                Action = Json.required "$" "action" root |> action
            })

    let private writeScope (writer: Utf8JsonWriter) scope =
        writer.WriteStartObject("scope")

        match scope with
        | RecordedGrantScope.Installation -> writer.WriteString("type", "INSTALLATION")
        | RecordedGrantScope.CaseReference reference ->
            writer.WriteString("type", "CASE")
            writer.WriteString("caseReference", reference)

        writer.WriteEndObject()

    let private writeAction (writer: Utf8JsonWriter) action =
        writer.WriteStartObject("action")

        match action with
        | RecordedAuthorityAction.ProvisionOwner -> writer.WriteString("type", "PROVISION_OWNER")
        | RecordedAuthorityAction.RegisterActor -> writer.WriteString("type", "REGISTER_ACTOR")
        | RecordedAuthorityAction.SetGrant(role, scope, active) ->
            writer.WriteString("type", "SET_GRANT")
            writer.WriteString("role", role)
            writeScope writer scope
            writer.WriteBoolean("active", active)
        | RecordedAuthorityAction.SetEnabled enabled ->
            writer.WriteString("type", "SET_ENABLED")
            writer.WriteBoolean("enabled", enabled)

        writer.WriteEndObject()

    let encode (request: AuthorityRequestRecord) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("eventId", request.EventId)
            writer.WriteStartObject("target")
            writer.WriteString("kind", request.Target.Kind)
            writer.WriteString("issuer", request.Target.Issuer)
            writer.WriteString("subject", request.Target.Subject)
            writer.WriteEndObject()
            writeAction writer request.Action
            writer.WriteEndObject())
