namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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

        (DatabaseSession.value<System.Guid> 0 row
         |> DatabaseSession.admitted ActorId.create),
        role,
        (DatabaseSession.optional<System.Guid> 2 row
         |> Option.map (DatabaseSession.admitted CaseId.create))

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
                        Read = DatabaseSession.namedRow
                        Append =
                            fun seen row ->
                                result {
                                    let! status =
                                        capture
                                            None
                                            VerificationFinding.CaseStatusProjection
                                            (fun () -> Projections.caseStatus row)

                                    let! progress =
                                        capture
                                            None
                                            VerificationFinding.CaseProgressProjection
                                            (fun () -> Projections.caseProgress row)

                                    let! changedAt =
                                        capture
                                            None
                                            VerificationFinding.CaseChangedAtProjection
                                            (fun () -> Projections.caseChangedAt row)

                                    let! searchText =
                                        capture
                                            None
                                            VerificationFinding.CaseSearchProjection
                                            (fun () -> Projections.caseSearchText row)

                                    let! notifiedOn =
                                        capture
                                            None
                                            VerificationFinding.CaseNotifiedOnProjection
                                            (fun () -> Projections.caseNotifiedOn row)

                                    let! claimedAmount =
                                        capture
                                            None
                                            VerificationFinding.CaseClaimedAmountProjection
                                            (fun () -> Projections.caseClaimedAmount row)

                                    let! claimedCurrency =
                                        capture
                                            None
                                            VerificationFinding.CaseClaimedCurrencyProjection
                                            (fun () -> Projections.caseClaimedCurrency row)

                                    let! payableAmount =
                                        capture
                                            None
                                            VerificationFinding.CasePayableAmountProjection
                                            (fun () -> Projections.casePayableAmount row)

                                    let! payableCurrency =
                                        capture
                                            None
                                            VerificationFinding.CasePayableCurrencyProjection
                                            (fun () -> Projections.casePayableCurrency row)

                                    let index: CaseIndex =
                                        {
                                            Status = status
                                            Progress = progress
                                            ChangedAt = changedAt
                                            SearchText = searchText
                                            NotifiedOn = notifiedOn
                                            Claimed =
                                                Projections.indexedMoney
                                                    claimedAmount
                                                    claimedCurrency
                                            Payable =
                                                Projections.indexedMoney
                                                    payableAmount
                                                    payableCurrency
                                        }

                                    let! record =
                                        capture None VerificationFinding.CaseProjection (fun () ->
                                            Projections.caseRowWithIndex installationId index row)

                                    return!
                                        ProjectionVerification.caseProjection
                                            installationId
                                            histories
                                            seen
                                            record
                                }
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
