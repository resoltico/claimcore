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

                    let! rows =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.digest_runs(actor_id,digest_date,claimed_at) SELECT actor_id,$2,clock_timestamp() FROM claimcore.actors WHERE actor_id=$1 AND enabled AND principal_kind=$3 AND digest_email IS NOT NULL ON CONFLICT DO NOTHING"
                            [| box (ActorId.value actor); box today; box principal |]
                            token

                    do! DatabaseSession.commit transaction token
                    return rows = 1
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

    let complete (source: NpgsqlDataSource) installation (today: DateOnly) actor outcome token =
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
                            "UPDATE claimcore.digest_runs SET outcome=$3,completed_at=GREATEST(claimed_at,clock_timestamp()) WHERE actor_id=$1 AND digest_date=$2 AND outcome IS NULL"
                            [|
                                box (ActorId.value actor)
                                box today
                                box (DigestOutcome.token outcome)
                            |]
                            token

                    if changed <> 1 then
                        raise (RecordCorruptException "Digest claim is absent or completed.")

                    do! DatabaseSession.commit transaction token
                })
