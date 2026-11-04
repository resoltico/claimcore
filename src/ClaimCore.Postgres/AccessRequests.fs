namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every requester upsert and identity-creating write holds the same transaction-scoped lock.
module internal AccessRequests =
    let parameters principal =
        let kind, issuer, subject = PrincipalKey.parts principal
        [| box (DurableNames.principalKind kind); box issuer; box subject |]

    let lockPrincipal connection principal token =
        // Length framing avoids ambiguous key concatenation; hash collisions only add serialization.
        let kind, issuer, subject = PrincipalKey.parts principal

        let key =
            $"{DurableNames.principalKind kind}:{issuer.Length}:{issuer}{subject.Length}:{subject}"

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
