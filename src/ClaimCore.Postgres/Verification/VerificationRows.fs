namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.Application.VerificationChecks

module internal VerificationRows =
    let capture sequence finding read =
        try
            VerificationChecks.capture sequence finding read
        with :? RecordCorruptException ->
            Error(divergence sequence finding)

    let private grant row =
        let role =
            Roles.tryParse (DatabaseSession.value<string> 1 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid projected role."))

        DatabaseSession.value<System.Guid> 0 row, role, DatabaseSession.optional<System.Guid> 2 row

    let cases connection installationId histories token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases ORDER BY case_id")
                    [||]
                    token

            return
                capture None VerificationFinding.CaseProjection (fun () ->
                    List.map Projections.caseRow rows)
                |> Result.bind (ProjectionVerification.cases installationId histories)
        }

    let authority connection expected token =
        task {
            let! actors =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors ORDER BY actor_id"
                    [||]
                    token

            let! grants =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,role,case_id FROM claimcore.grants ORDER BY actor_id,role,case_id"
                    [||]
                    token

            return
                capture None VerificationFinding.AuthorityProjection (fun () ->
                    List.map Projections.actorSummary actors, List.map grant grants)
                |> Result.bind (fun (actors, grants) ->
                    ProjectionVerification.authority expected actors grants)
        }
