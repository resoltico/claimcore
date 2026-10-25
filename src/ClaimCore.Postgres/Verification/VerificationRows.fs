namespace ClaimCore.Postgres

open ClaimCore.Contract

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
            RoleTokens.parse (DatabaseSession.value<string> 1 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid projected role."))

        DatabaseSession.value<System.Guid> 0 row, role, DatabaseSession.optional<System.Guid> 2 row

    let private fold
        connection
        sql
        decode
        append
        initial
        finding
        (token: System.Threading.CancellationToken)
        =
        task {
            use command = DatabaseSession.command connection sql [||]
            use! reader = command.ExecuteReaderAsync(token)
            let mutable state = Ok initial
            let mutable reading = true

            while reading && Result.isOk state do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    let row = Array.zeroCreate<obj> reader.FieldCount
                    reader.GetValues row |> ignore

                    state <-
                        state
                        |> Result.bind (fun state ->
                            capture None finding (fun () -> decode row)
                            |> Result.bind (append state))

            return state
        }

    let cases connection installationId histories token =
        task {
            let! seen =
                fold
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases ORDER BY case_id")
                    Projections.caseRow
                    (ProjectionVerification.caseProjection installationId histories)
                    Set.empty
                    VerificationFinding.CaseProjection
                    token

            return seen |> Result.bind (ProjectionVerification.finishCases histories)
        }

    let authority connection expected token =
        task {
            let! actors =
                fold
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors ORDER BY actor_id"
                    Projections.actorRow
                    ProjectionVerification.actor
                    Map.empty
                    VerificationFinding.AuthorityProjection
                    token

            match actors with
            | Error error -> return Error error
            | Ok actors ->
                let! actual =
                    fold
                        connection
                        "SELECT actor_id,role,case_id FROM claimcore.grants ORDER BY actor_id,role,case_id"
                        grant
                        ProjectionVerification.grant
                        actors
                        VerificationFinding.AuthorityProjection
                        token

                return actual |> Result.bind (ProjectionVerification.finishAuthority expected)
        }
