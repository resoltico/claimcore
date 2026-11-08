namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every requester upsert and identity-creating write holds the same transaction-scoped lock.
module internal AccessRequests =
    let parameters principal =
        let kind, issuer, subject = PrincipalKey.parts principal

        [|
            box ((Vocabulary.token PrincipalKind.vocabulary) kind)
            box issuer
            box subject
        |]

    let lockPrincipal connection principal token =
        // Length framing avoids ambiguous key concatenation; hash collisions only add serialization.
        let kind, issuer, subject = PrincipalKey.parts principal

        let key =
            $"{(Vocabulary.token PrincipalKind.vocabulary) kind}:{issuer.Length}:{issuer}{subject.Length}:{subject}"

        DatabaseSession.execute
            connection
            "SELECT pg_advisory_xact_lock(hashtextextended($1,173701))"
            [| box key |]
            token

    let delete connection principal token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.access_request WHERE principal_kind=$1 AND issuer=$2 AND subject=$3"
            (parameters principal)
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

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.access_request WHERE (issuer,principal_kind,subject) IN (SELECT issuer,principal_kind,subject FROM claimcore.access_request WHERE last_seen<$1::timestamptz-INTERVAL '30 days' ORDER BY last_seen,issuer,principal_kind,subject LIMIT 50)"
                    [| box observed |]
                    token

            let hint value =
                DatabaseSession.parameter (value |> Option.map PartyName.format)

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.access_request(principal_kind,issuer,subject,first_seen,last_seen,name,preferred_username,email) VALUES ($1,$2,$3,$4,$4,$5,$6,$7) ON CONFLICT (issuer,principal_kind,subject) DO UPDATE SET last_seen=greatest(access_request.last_seen,EXCLUDED.last_seen),name=EXCLUDED.name,preferred_username=EXCLUDED.preferred_username,email=EXCLUDED.email"
                    (Array.append
                        (parameters principal)
                        [|
                            box observed
                            hint claims.Name
                            hint claims.PreferredUsername
                            hint claims.Email
                        |])
                    token

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.access_request WHERE (issuer,principal_kind,subject) IN (SELECT issuer,principal_kind,subject FROM claimcore.access_request ORDER BY last_seen DESC,issuer,principal_kind,subject OFFSET 1000)"
                    [||]
                    token

            return ()
        }

    let page connection (query: ClaimCore.Application.AccessRequestPageQuery) token =
        task {
            let seen, key =
                match query.After with
                | Some after -> Some after.LastSeen, parameters after.Principal
                | None ->
                    None,
                    [|
                        DatabaseSession.nullValue
                        DatabaseSession.nullValue
                        DatabaseSession.nullValue
                    |]

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT principal_kind,issuer,subject,first_seen,last_seen,name,preferred_username,email FROM claimcore.access_request WHERE last_seen >= $1::timestamptz-INTERVAL '30 days' AND ($2::timestamptz IS NULL OR last_seen<$2 OR (last_seen=$2 AND (issuer,principal_kind,subject)>($4,$3,$5))) ORDER BY last_seen DESC,issuer,principal_kind,subject LIMIT $6"
                    [|
                        box query.ObservedAt
                        DatabaseSession.parameter seen
                        key[0]
                        key[1]
                        key[2]
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

                    {
                        AccessRequest.Principal = principal
                        FirstSeen = DatabaseSession.instant 3 row
                        LastSeen = DatabaseSession.instant 4 row
                        Claims =
                            RequesterClaims.admit
                                (DatabaseSession.optional<string> 5 row)
                                (DatabaseSession.optional<string> 6 row)
                                (DatabaseSession.optional<string> 7 row)
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
