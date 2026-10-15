namespace ClaimCore.Application

open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal CursorQuery =
    | Cases
    | History of reference: string * detail: HistoryDetail
    | Actors

module internal Cursors =
    let private lifetimeTicks = TimeSpan.FromMinutes(15.0).Ticks

    let private digest value =
        CanonicalJson.encode value |> SHA256.HashData

    let private scopeKey =
        function
        | GrantScope.Installation -> ""
        | GrantScope.Case id -> id.ToString("D")

    let private grantOrder (left: ActorGrant) (right: ActorGrant) =
        let role = String.CompareOrdinal(Roles.token left.Role, Roles.token right.Role)

        if role <> 0 then
            role
        else
            String.CompareOrdinal(scopeKey left.Scope, scopeKey right.Scope)

    let private grantValue (grant: ActorGrant) =
        let scope =
            match grant.Scope with
            | GrantScope.Installation ->
                CanonicalValue.Object [ "kind", CanonicalValue.String "INSTALLATION" ]
            | GrantScope.Case id ->
                CanonicalValue.Object
                    [
                        "kind", CanonicalValue.String "CASE"
                        "caseId", CanonicalValue.String(id.ToString("D"))
                    ]

        CanonicalValue.Object
            [ "role", CanonicalValue.String(Roles.token grant.Role); "scope", scope ]

    let private queryValue =
        function
        | CursorQuery.Cases ->
            CanonicalValue.Object
                [
                    "endpoint", CanonicalValue.String "case.list"
                    "order", CanonicalValue.String "REFERENCE_ASCENDING"
                ]
        | CursorQuery.Actors ->
            CanonicalValue.Object
                [
                    "endpoint", CanonicalValue.String "authority.list"
                    "order", CanonicalValue.String "ACTOR_ID_ASCENDING"
                ]
        | CursorQuery.History(reference, detail) ->
            CanonicalValue.Object
                [
                    "endpoint", CanonicalValue.String "case.history"
                    "caseReference", CanonicalValue.String(CaseReference.key reference)
                    "detail",
                    CanonicalValue.String(if detail = HistoryDetail.Full then "FULL" else "SUMMARY")
                    "order", CanonicalValue.String "REVISION_ASCENDING"
                ]

    let private binding (installationId: Guid) (actor: ActorAuthority) query =
        let principal = PrincipalKey.describe actor.Principal

        let grants =
            actor.Grants
            |> List.distinct
            |> List.sortWith grantOrder
            |> List.map grantValue
            |> CanonicalValue.Array
            |> digest

        CanonicalValue.Object
            [
                "installationId", CanonicalValue.String(installationId.ToString("D"))
                "actorId", CanonicalValue.String(actor.ActorId.ToString("D"))
                "enabled", CanonicalValue.Boolean actor.Enabled
                "principal",
                CanonicalValue.Object
                    [
                        "kind", CanonicalValue.String principal.Kind
                        "issuer", CanonicalValue.String principal.Issuer
                        "subject", CanonicalValue.String principal.Subject
                    ]
                "grants", CanonicalValue.String(Convert.ToHexStringLower grants)
                "query", queryValue query
            ]
        |> digest

    let encode key installationId actor query (observedAt: DateTimeOffset) position =
        CursorRecord.encode
            key
            {
                IssuedAtTicks = observedAt.UtcTicks
                Binding = binding installationId actor query
                Position = position
            }

    /// MAC and structural validation happen before position is returned to an endpoint parser.
    let decode key installationId actor query (observedAt: DateTimeOffset) token =
        match CursorRecord.decode key token with
        | Error _ -> Error Rejection.InvalidCursor
        | Ok envelope ->
            let expected = binding installationId actor query

            if
                envelope.IssuedAtTicks <= observedAt.UtcTicks
                && observedAt.UtcTicks < envelope.IssuedAtTicks + lifetimeTicks
                && CryptographicOperations.FixedTimeEquals(expected, envelope.Binding)
            then
                Ok envelope.Position
            else
                Error Rejection.InvalidCursor
