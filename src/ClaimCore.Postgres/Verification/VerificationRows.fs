namespace ClaimCore.Postgres

open ClaimCore.Domain

open ClaimCore.Application
open ClaimCore.Application.VerificationChecks

[<NoEquality; NoComparison>]
type private ProjectionScan<'value, 'state> =
    {
        Read: Npgsql.NpgsqlDataReader -> 'value
        Append: 'state -> 'value -> Result<'state, VerificationDivergence>
        Initial: 'state
        Finding: VerificationFinding
    }

module internal VerificationRows =
    let capture sequence finding read =
        try
            VerificationChecks.capture sequence finding read
        with :? RecordCorruptException ->
            Error(divergence sequence finding)

    let private grant row =
        let role =
            ClaimCore.RecordFormat.DurableNames.tryRole (DatabaseSession.value<string> 1 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid projected role."))

        DatabaseSession.value<System.Guid> 0 row, role, DatabaseSession.optional<System.Guid> 2 row

    let private fold
        connection
        sql
        (scan: ProjectionScan<'value, 'state>)
        (token: System.Threading.CancellationToken)
        =
        task {
            use statement = DatabaseSession.statement connection sql [||]
            use! reader = statement.ExecuteReaderAsync(token)
            let mutable state = Ok scan.Initial
            let mutable reading = true

            while reading && Result.isOk state do
                let! found = reader.ReadAsync(token)

                if not found then
                    reading <- false
                else
                    state <-
                        state
                        |> Result.bind (fun state ->
                            capture None scan.Finding (fun () -> scan.Read reader)
                            |> Result.bind (scan.Append state))

            return state
        }

    let cases connection installationId histories token =
        task {
            let! seen =
                fold
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases ORDER BY case_id")
                    {
                        Read = DatabaseSession.namedRow >> Projections.caseRow installationId
                        Append = (ProjectionVerification.caseProjection installationId histories)
                        Initial = Set.empty
                        Finding = VerificationFinding.CaseProjection
                    }
                    token

            return seen |> Result.bind (ProjectionVerification.finishCases histories)
        }

    let authority connection expected token =
        task {
            let! actors =
                fold
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors ORDER BY actor_id"
                    {
                        Read = DatabaseSession.row >> Projections.actorRow
                        Append = ProjectionVerification.actor
                        Initial = Map.empty
                        Finding = VerificationFinding.AuthorityProjection
                    }
                    token

            match actors with
            | Error error -> return Error error
            | Ok actors ->
                let! actual =
                    fold
                        connection
                        "SELECT actor_id,role,case_id FROM claimcore.grants ORDER BY actor_id,role,case_id"
                        {
                            Read = DatabaseSession.row >> grant
                            Append = ProjectionVerification.grant
                            Initial = actors
                            Finding = VerificationFinding.AuthorityProjection
                        }
                        token

                return actual |> Result.bind (ProjectionVerification.finishAuthority expected)
        }
