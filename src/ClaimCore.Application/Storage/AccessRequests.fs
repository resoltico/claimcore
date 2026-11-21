namespace ClaimCore.Application.Storage

open System

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every requester upsert and identity-creating write holds the same transaction-scoped lock.
module internal AccessRequests =
    type PageQuery =
        {
            After: AccessRequestPosition option
            ObservedAt: DateTimeOffset
            Limit: int
        }

    let expiredPredicate alias =
        $"{alias}last_seen<$1::timestamptz-INTERVAL '30 days'"

    let parameters principal =
        let kind, issuer, subject = PrincipalKey.parts principal

        [|
            box ((Vocabulary.token PrincipalKind.vocabulary) kind)
            box issuer
            box subject
        |]

    let delete connection principal token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.access_request WHERE principal_kind=$1 AND issuer=$2 AND subject=$3 AND principal_key=$4"
            (Array.append (parameters principal) [| box (PrincipalKey.naturalKey principal) |])
            token

    let refresh connection principal (claims: RequesterClaims) observed token =
        task {
            // This lock bounds capacity across different principals. It is never acquired by ledger writes.
            let! _ =
                DatabaseSession.execute
                    connection
                    "SELECT pg_advisory_xact_lock(173702,0)"
                    [||]
                    token

            let hint value =
                DatabaseSession.parameter (value |> Option.map PartyName.format)

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.access_request(principal_kind,issuer,subject,first_seen,last_seen,name,preferred_username,email,email_verified,principal_key) VALUES ($1,$2,$3,$4,$4,$5,$6,$7,$8,$9) ON CONFLICT (principal_key) DO UPDATE SET last_seen=greatest(access_request.last_seen,EXCLUDED.last_seen),name=EXCLUDED.name,preferred_username=EXCLUDED.preferred_username,email=EXCLUDED.email,email_verified=EXCLUDED.email_verified WHERE access_request.principal_kind=EXCLUDED.principal_kind AND access_request.issuer=EXCLUDED.issuer AND access_request.subject=EXCLUDED.subject"
                    (Array.append
                        (parameters principal)
                        [|
                            box observed
                            hint claims.Name
                            hint claims.PreferredUsername
                            hint claims.Email
                            box claims.EmailVerified
                            box (PrincipalKey.naturalKey principal)
                        |])
                    token

            if changed <> 1 then
                raise (
                    RecordCorruptException
                        "Access-request principal key disagrees with its identity."
                )

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.access_request WHERE principal_key IN (SELECT principal_key FROM claimcore.access_request ORDER BY last_seen DESC,principal_key OFFSET 1000)"
                    [||]
                    token

            return ()
        }

    let page connection (query: PageQuery) token =
        task {
            let seen, key =
                match query.After with
                | Some after -> Some after.LastSeen, Some(PrincipalKey.naturalKey after.Principal)
                | None -> None, None

            let! rows =
                DatabaseSession.rows
                    connection
                    ("SELECT principal_kind,issuer,subject,first_seen,last_seen,name,preferred_username,email,email_verified,principal_key FROM claimcore.access_request WHERE NOT ("
                     + expiredPredicate ""
                     + ") AND ($2::timestamptz IS NULL OR last_seen<$2 OR (last_seen=$2 AND principal_key>$3)) ORDER BY last_seen DESC,principal_key LIMIT $4")
                    [|
                        box query.ObservedAt
                        DatabaseSession.parameter seen
                        DatabaseSession.parameter key
                        box (query.Limit + 1)
                    |]
                    token

            let items =
                rows
                |> List.map (fun row ->
                    let kind =
                        DatabaseSession.value<string> 0 row
                        |> (Vocabulary.tryParse PrincipalKind.vocabulary)
                        |> Option.defaultWith (fun () ->
                            raise (
                                RecordCorruptException "Invalid access-request principal kind."
                            ))

                    let principal =
                        PrincipalKey.ofKind
                            kind
                            (DatabaseSession.value<string> 1 row)
                            (DatabaseSession.value<string> 2 row)
                        |> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Invalid access-request principal."))

                    if
                        DatabaseSession.value<string> 9 row <> PrincipalKey.naturalKey principal
                    then
                        raise (
                            RecordCorruptException
                                "Access-request principal key disagrees with its identity."
                        )

                    {
                        AccessRequest.Principal = principal
                        FirstSeen = DatabaseSession.instant 3 row
                        LastSeen = DatabaseSession.instant 4 row
                        Claims =
                            RequesterClaims.admit
                                (DatabaseSession.optional<string> 5 row)
                                (DatabaseSession.optional<string> 6 row)
                                (DatabaseSession.optional<string> 7 row)
                                (DatabaseSession.value<bool> 8 row)
                    })

            let visible = List.truncate query.Limit items

            return
                ({
                    Items = visible
                    Next =
                        if items.Length > query.Limit then
                            List.tryLast visible
                            |> Option.map (fun value ->
                                {
                                    AccessRequestPosition.LastSeen = value.LastSeen
                                    Principal = value.Principal
                                })
                        else
                            None
                }
                : Page<AccessRequest, AccessRequestPosition>)
        }
