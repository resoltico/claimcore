namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Owner-only maintenance. The ledger head lock excludes application writers throughout the scan.
module internal ProjectionRebuild =
    let private replaceCases connection rows token =
        task {
            for KeyValue(id, (owned: CaseSubjectRows, index)) in rows do
                let snapshot = owned.Snapshot

                match snapshot.State with
                | CaseState.Erased ->
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.cases(case_id,case_reference,revision,state,changed_at) VALUES ($1,$2,$3,'ERASED',$4)"
                            [|
                                box (CaseId.value id)
                                box (CaseReference.format snapshot.Reference)
                                box (Revision.value snapshot.Revision)
                                box owned.ChangedAt
                            |]
                            token

                    ()
                | _ ->
                    do!
                        Persistence.case
                            connection
                            (CaseProjection.Insert(id, snapshot, index))
                            token

                for payment in owned.Payments do
                    do! PaymentStorage.restore connection payment token
        }

    let private replacePeople connection rows token =
        task {
            for row in rows |> Map.values do
                do! Persistence.peopleActor connection row token
        }

    let private replaceWork connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, history) in state.Work do
                let parent = Map.find id state.Cases

                let owned =
                    {
                        WorkSubjectState.CaseId = id
                        Revision = history.Revision
                        Snapshot = history.Snapshot
                        ChangedAt = history.ChangedAt
                        RecordedBy = history.RecordedBy
                        SummaryChangedAt = history.SummaryChangedAt
                        SummaryRecordedBy = history.SummaryRecordedBy
                        Erased = parent.State = CaseState.Erased
                    }

                let rows =
                    WorkSubject.rows owned
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Incomplete work after replay."))

                do! Persistence.workChange connection (WorkChangeProjection.Work rows) token

            let reminders =
                state.Reminders
                |> Map.toList
                |> List.map (fun (id, history) ->
                    let owned =
                        {
                            ReminderSubjectState.ReminderId = id
                            CaseId = history.CaseId
                            Revision = history.Revision
                            Status = Some history.State
                            Snapshot = history.Snapshot
                            RecordedAt = history.RecordedAt
                            RecordedBy = history.RecordedBy
                            Erased = (Map.find history.CaseId state.Cases).State = CaseState.Erased
                        }

                    ReminderSubject.rows owned
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Incomplete reminder after replay.")))

            do! Persistence.workChange connection (WorkChangeProjection.Reminder reminders) token

        }

    let private replaceNotes connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, history) in state.Notes do
                let owned =
                    {
                        NoteSubjectState.NoteId = id
                        CaseId = history.CaseId
                        Removed = history.Removed
                        Snapshot = history.Snapshot
                        Created =
                            {
                                Position = history.CreatedPosition
                                OperationId = OperationId.create (NoteId.value id) |> Option.get
                                ActorId = history.RecordedBy
                                RecordedAt = history.RecordedAt
                            }
                        Erased = (Map.find history.CaseId state.Cases).State = CaseState.Erased
                    }

                let rows =
                    NoteSubject.rows owned
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Incomplete note after replay."))

                do! Persistence.workChange connection (WorkChangeProjection.Note rows) token
        }

    let private replaceGrants connection rows token =
        task {
            for row in rows |> Map.values do
                do! Persistence.peopleGrants connection row token
        }

    let rebuild
        (source: NpgsqlDataSource)
        (expected: InstallationCheckpoint)
        (token: CancellationToken)
        =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    ServerSupport.require connection.PostgreSqlVersion
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! _ = Transactions.head connection token
                    do! RegisterMaintenanceStore.requireActive connection token
                    let! installation, _ = Baseline.require connection token

                    let! scanned =
                        match CheckpointEvidence.installation installation expected with
                        | Error finding -> System.Threading.Tasks.Task.FromResult(Error finding)
                        | Ok() ->
                            task {
                                let! scanned =
                                    Verification.authoritative
                                        connection
                                        installation
                                        (Some expected.Position)
                                        token

                                match scanned with
                                | Error finding -> return Error finding
                                | Ok state ->
                                    let! frontier =
                                        CheckpointEvidence.frontier
                                            connection
                                            (Some expected)
                                            token

                                    return frontier |> Result.map (fun () -> state)
                            }

                    match scanned with
                    | Error finding -> return Error finding
                    | Ok state ->
                        let rows =
                            Replay.caseRows state
                            |> Result.defaultWith (fun _ ->
                                raise (
                                    RecordCorruptException
                                        "Case rows are incomplete after authoritative replay."
                                ))

                        let people =
                            Replay.peopleRows state
                            |> Result.defaultWith (fun _ ->
                                raise (
                                    RecordCorruptException
                                        "People rows are incomplete after authoritative replay."
                                ))

                        let! before = Verification.projections connection installation state token
                        // DELETE preserves old MVCC snapshots; TRUNCATE and table swapping do not.
                        for table in
                            [
                                "grants"
                                "notes"
                                "reminders"
                                "case_work"
                                "case_payments"
                                "cases"
                                "actors"
                            ] do
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    ("DELETE FROM claimcore." + table)
                                    [||]
                                    token

                            ()

                        do! replacePeople connection people token
                        do! replaceCases connection rows token
                        do! replaceWork connection state token
                        do! replaceNotes connection state token
                        do! replaceGrants connection people token

                        let! validated =
                            Verification.projections connection installation state token

                        match validated with
                        | Error finding -> return Error finding
                        | Ok() ->
                            do! DatabaseSession.commit transaction token

                            return
                                Ok
                                    {
                                        ProjectionRebuildReport.Position = state.Head
                                        PreviousDivergence =
                                            match before with
                                            | Error finding -> Some finding
                                            | Ok() -> None
                                    }
                })
