namespace ClaimCore.Application.Storage

open System
open System.Data
open ClaimCore.Application
open ClaimCore.Application.Execution
open ClaimCore.Application.Reads
open ClaimCore.Domain
open ClaimCore.RecordFormat
open Npgsql

module internal DigestStorage =
    let private principal =
        Vocabulary.token PrincipalKind.vocabulary ReminderDigest.principalKind

    let private admitted (installation: Installation) connection token =
        task {
            let! _ = Transactions.configure connection Transactions.timeouts token
            do! Baseline.requireExpected installation connection token
        }

    let candidates (source: NpgsqlDataSource) installation (today: DateOnly) token =
        DatabaseSession.read source token (fun connection ->
            task {
                do! admitted installation connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT a.actor_id FROM claimcore.actors a WHERE a.enabled AND a.principal_kind=$1 AND a.digest_email IS NOT NULL AND NOT EXISTS(SELECT 1 FROM claimcore.digest_runs d WHERE d.actor_id=a.actor_id AND d.digest_date=$2) ORDER BY a.actor_id"
                        [| box principal; box today |]
                        token

                return
                    rows
                    |> List.map (
                        DatabaseSession.value<Guid> 0 >> DatabaseSession.admitted ActorId.create
                    )
            })

    let claim (source: NpgsqlDataSource) installation (today: DateOnly) actor token =
        DatabaseSession.transaction
            source
            IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! admitted installation connection token
                    let claimId = Guid.NewGuid()

                    let! rows =
                        DatabaseSession.execute
                            connection
                            """WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now)
                           INSERT INTO claimcore.digest_runs(actor_id,digest_date,claim_id,claimed_at,deadline)
                           SELECT actor_id,$2,$4,now,now+$5 FROM claimcore.actors CROSS JOIN clock
                           WHERE actor_id=$1 AND enabled AND principal_kind=$3 AND digest_email IS NOT NULL
                           ON CONFLICT (actor_id,digest_date) DO NOTHING"""
                            [|
                                box (ActorId.value actor)
                                box today
                                box principal
                                box claimId
                                box ReminderDigest.attemptDuration
                            |]
                            token

                    do! DatabaseSession.commit transaction token
                    return if rows = 1 then Some claimId else None
                })

    let start (source: NpgsqlDataSource) installation claimId token =
        let requestedAt = System.Diagnostics.Stopwatch.GetTimestamp()

        DatabaseSession.transaction
            source
            IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! admitted installation connection token

                    let! result =
                        DatabaseSession.rows
                            connection
                            """WITH claim AS MATERIALIZED (
                               SELECT claim_id FROM claimcore.digest_runs WHERE claim_id=$1 FOR UPDATE
                           ), clock AS MATERIALIZED (
                               SELECT clock_timestamp() AS now FROM claim
                           )
                           UPDATE claimcore.digest_runs d SET started_at=clock.now FROM clock
                           WHERE claim_id=$1 AND started_at IS NULL AND outcome IS NULL
                             AND clock.now>=claimed_at AND clock.now<deadline
                           RETURNING deadline-clock.now"""
                            [| box claimId |]
                            token

                    do! DatabaseSession.commit transaction token

                    let elapsed = System.Diagnostics.Stopwatch.GetElapsedTime requestedAt

                    return
                        result
                        |> List.tryExactlyOne
                        |> Option.map (fun row -> DatabaseSession.value<TimeSpan> 0 row - elapsed)
                })

    let prepare (source: NpgsqlDataSource) installation (basis: WorkDateBasis) actor ct =
        DatabaseSession.transaction
            source
            IsolationLevel.RepeatableRead
            ct
            (fun connection transaction ->
                task {
                    do! admitted installation connection ct

                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact,digest_email FROM claimcore.actors WHERE actor_id=$1 AND enabled AND principal_kind=$2 AND digest_email IS NOT NULL"
                            [| box (ActorId.value actor); box principal |]
                            ct

                    let! prepared =
                        task {
                            match
                                rows
                                |> List.tryExactlyOne
                                |> Option.map (Projections.actorRowWithDigest 7)
                            with
                            | None -> return None
                            | Some person ->
                                let principal = person.Principal

                                let! access =
                                    Projections.actorWithVisibility connection principal ct

                                match
                                    Access.visibility principal access,
                                    person.Profile |> Option.bind _.DigestEmail
                                with
                                | Some visibility, Some recipient ->
                                    let selection =
                                        {
                                            CaseSelection.DateBasis = basis
                                            Visibility = visibility
                                            ActorId = actor
                                            ReadRoles = Access.readRoles principal
                                            ForbiddenRoles = Access.invalidRoles principal
                                            Filter = CaseListFilter.empty
                                        }

                                    let allowedCases = CaseSelectionSql.predicate selection
                                    let due = CaseSelectionSql.suffix 1

                                    let reminders =
                                        CaseSelectionSql.selectionPredicate
                                            ReminderDigest.selection

                                    let date = DatabaseSession.dateExpression "r.due"

                                    let! rows =
                                        DatabaseSession.rows
                                            connection
                                            ($"SELECT r.kind,{date} FROM claimcore.reminders r JOIN claimcore.cases c ON c.case_id=r.case_id WHERE ({allowedCases}) AND ({reminders}) AND r.due<={due}::date")
                                            (Array.append
                                                (CaseSelectionSql.parameters selection)
                                                [|
                                                    box (
                                                        ReminderDigest.dueCutoff basis.BusinessDate
                                                    )
                                                |])
                                            ct

                                    let reminders =
                                        rows
                                        |> Seq.map (fun row ->
                                            let kind =
                                                DatabaseSession.value<string> 0 row
                                                |> Vocabulary.tryParse ReminderKind.vocabulary
                                                |> Option.defaultWith (fun () ->
                                                    raise (
                                                        RecordCorruptException
                                                            "Invalid digest reminder kind."
                                                    ))

                                            let due = DatabaseSession.calendarDate 1 row
                                            kind, due)

                                    return
                                        Some(
                                            recipient,
                                            ReminderDigest.counts basis.BusinessDate reminders
                                        )
                                | _ -> return None
                        }

                    do! DatabaseSession.commit transaction ct
                    return prepared
                })

    let private decodeOutcome value =
        Vocabulary.tryParse DigestOutcome.vocabulary value
        |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid digest outcome."))

    let private settled source installation claimId token =
        DatabaseSession.read source token (fun connection ->
            task {
                do! admitted installation connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT outcome FROM claimcore.digest_runs WHERE claim_id=$1"
                        [| box claimId |]
                        token

                return
                    rows
                    |> List.tryExactlyOne
                    |> Option.bind (DatabaseSession.optional<string> 0)
                    |> Option.map decodeOutcome
            })

    let complete (source: NpgsqlDataSource) installation claimId outcome token =
        let write () =
            DatabaseSession.transaction
                source
                IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        do! admitted installation connection token

                        let! changed =
                            DatabaseSession.execute
                                connection
                                """UPDATE claimcore.digest_runs
                               SET outcome=$2,completed_at=GREATEST(claimed_at,clock_timestamp())
                               WHERE claim_id=$1 AND outcome IS NULL"""
                                [| box claimId; box (DigestOutcome.token outcome) |]
                                token

                        do! DatabaseSession.commit transaction token
                        return changed = 1
                    })

        task {
            try
                let! committed = write ()

                if committed then
                    return Some outcome
                else
                    return! settled source installation claimId token
            with :? AdministrationOutcomeUnknownException ->
                return! settled source installation claimId token
        }

    let reconcile (source: NpgsqlDataSource) installation token =
        DatabaseSession.transaction
            source
            IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! admitted installation connection token

                    let! _ =
                        DatabaseSession.execute
                            connection
                            """WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now),
                           stale AS MATERIALIZED (
                               SELECT claim_id FROM claimcore.digest_runs,clock
                               WHERE outcome IS NULL AND deadline<=clock.now
                               ORDER BY deadline,claim_id LIMIT 128 FOR UPDATE OF digest_runs SKIP LOCKED
                           )
                           UPDATE claimcore.digest_runs d SET outcome=$1,completed_at=clock.now
                           FROM stale,clock WHERE d.claim_id=stale.claim_id AND d.outcome IS NULL"""
                            [| box (DigestOutcome.token DigestOutcome.Unknown) |]
                            token

                    do! DatabaseSession.commit transaction token
                })

    let summary source installation token =
        DatabaseSession.read source token (fun connection ->
            task {
                do! admitted installation connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now)
                           SELECT outcome, outcome IS NULL AND deadline<=clock.now AS expired,count(*)
                           FROM claimcore.digest_runs,clock GROUP BY outcome,expired"""
                        [||]
                        token

                return
                    rows
                    |> List.fold
                        (fun state row ->
                            let count = DatabaseSession.value<int64> 2 row

                            match DatabaseSession.optional<string> 0 row with
                            | Some outcome ->
                                { state with
                                    Terminal =
                                        Map.add (decodeOutcome outcome) count state.Terminal
                                }
                            | None when DatabaseSession.value<bool> 1 row ->
                                { state with Expired = count }
                            | None -> { state with InFlight = count })
                        {
                            InFlight = 0L
                            Expired = 0L
                            Terminal = Map.empty
                        }
            })
