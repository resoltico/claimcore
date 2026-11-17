namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain

/// Complete rights and accepting provenance are loaded only for a new people decision.
module internal PeopleStateStorage =
    /// Paged reviews acquire only the version, not an unbounded grant/provenance collection.
    let revision connection principal token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT revision FROM claimcore.actors WHERE principal_kind=$1 AND issuer=$2 AND subject=$3 AND principal_key=$4"
                    (Array.append
                        (AccessRequests.parameters principal)
                        [| box (PrincipalKey.naturalKey principal) |])
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                let revision =
                    DatabaseSession.value<int64> 0 row |> DatabaseSession.admitted Revision.create

                if revision = Revision.zero then
                    raise (RecordCorruptException "Invalid actor revision.")

                return Some revision
            | _ -> return raise (RecordCorruptException "Duplicate actor identity.")
        }
