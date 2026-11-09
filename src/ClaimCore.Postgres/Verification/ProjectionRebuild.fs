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
    let private snapshot id (history: CaseHistory) =
        match history.Snapshot, history.State, history.Reference with
        | Some snapshot, _, _ -> snapshot
        | None, CaseState.Erased, Some reference ->
            {
                CaseId = id
                Reference = reference
                Revision = history.Revision
                State = CaseState.Erased
            }
        | _ -> raise (RecordCorruptException "Authoritative case reconstruction is incomplete.")

    let private replaceCases connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, history) in state.Cases do
                let snapshot = snapshot id history

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
                                box history.ChangedAt
                            |]
                            token

                    ()
                | _ ->
                    do!
                        Persistence.case
                            connection
                            (CaseProjection.Insert(
                                id,
                                snapshot,
                                CaseIndex.create history.ChangedAt snapshot
                            ))
                            token
        }

    let private replacePayments connection (state: Replay.Scan) token =
        task {
            for history in state.Cases |> Map.values do
                for payment in history.PaymentProjections |> Map.values do
                    do! PaymentStorage.restore connection payment token
        }

    let private replaceAuthority connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, actor) in state.Actors do
                do!
                    Persistence.authority
                        connection
                        (AuthorityProjection.Register(id, actor.Principal))
                        token

                do!
                    Persistence.authority
                        connection
                        (AuthorityProjection.SetEnabled(id, actor.Enabled))
                        token

                match actor.Actor.Profile with
                | None -> ()
                | Some profile ->
                    do!
                        Persistence.authority
                            connection
                            (AuthorityProjection.SetProfile(id, profile))
                            token

        }

    let private replaceWork connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, history) in state.Work do
                match history.Snapshot with
                | Some snapshot when snapshot.Revision > Revision.zero ->
                    do!
                        Persistence.workChange
                            connection
                            (WorkChangeProjection.Work
                                {
                                    Snapshot = snapshot
                                    ChangedAt = history.ChangedAt.Value
                                    RecordedBy = history.RecordedBy.Value
                                    SummaryChangedAt = history.SummaryChangedAt
                                    SummaryRecordedBy = history.SummaryRecordedBy
                                })
                            token
                | Some _ -> ()
                | None ->
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.case_work(case_id,revision,changed_at,recorded_by) VALUES($1,$2,$3,$4)"
                            [|
                                box (CaseId.value id)
                                box (Revision.value history.Revision)
                                DatabaseSession.parameter history.ChangedAt
                                DatabaseSession.parameter (
                                    history.RecordedBy |> Option.map ActorId.value
                                )
                            |]
                            token

                    ()

            for KeyValue(id, history) in state.Reminders do
                match history.Snapshot with
                | Some snapshot ->
                    do!
                        Persistence.workChange
                            connection
                            (WorkChangeProjection.Reminder
                                {
                                    Effects = ReminderEffects.Created snapshot
                                    ChangedAt = history.RecordedAt
                                    RecordedBy = history.RecordedBy
                                })
                            token
                | None ->
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.reminders(reminder_id,case_id,revision,changed_at,recorded_by) VALUES($1,$2,$3,$4,$5)"
                            [|
                                box (ReminderId.value id)
                                box (CaseId.value history.CaseId)
                                box (Revision.value history.Revision)
                                box history.RecordedAt
                                box (ActorId.value history.RecordedBy)
                            |]
                            token

                    ()
        }

    let private replaceNotes connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, history) in state.Notes do
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.notes(note_id,case_id,created_sequence,created_effect_index,text) VALUES($1,$2,$3,$4,$5)"
                        [|
                            box (NoteId.value id)
                            box (CaseId.value history.CaseId)
                            box (Sequence.value (EffectPosition.sequence history.CreatedPosition))
                            box (EffectPosition.index history.CreatedPosition)
                            DatabaseSession.parameter (
                                history.Snapshot
                                |> Option.bind Note.text
                                |> Option.map NoteText.format
                            )
                        |]
                        token

                ()
        }

    let private replaceGrants connection (state: Replay.Scan) token =
        task {
            for KeyValue(id, actor) in state.Actors do
                for grant in actor.Grants do
                    let caseId =
                        match grant.Scope with
                        | Scope.Installation -> None
                        | Scope.Case id -> Some id

                    do!
                        Persistence.authority
                            connection
                            (AuthorityProjection.AddGrant(
                                id,
                                grant.Role,
                                caseId,
                                Map.find (id, grant.Role, grant.Scope) state.GrantSources
                            ))
                            token

        }

    let rebuild (source: NpgsqlDataSource) expected (token: CancellationToken) =
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
                        Verification.authoritative connection installation (Some expected) token

                    match scanned with
                    | Error finding -> return Error finding
                    | Ok state ->
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

                        do! replaceAuthority connection state token
                        do! replaceCases connection state token
                        do! replacePayments connection state token
                        do! replaceWork connection state token
                        do! replaceNotes connection state token
                        do! replaceGrants connection state token

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

    let invalidateRestoredSessions (source: NpgsqlDataSource) (token: CancellationToken) =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    ServerSupport.require connection.PostgreSqlVersion
                    let! _ = Baseline.require connection token
                    let! _ = Transactions.head connection token

                    let! deleted =
                        DatabaseSession.execute
                            connection
                            "DELETE FROM claimcore.sessions"
                            [||]
                            token

                    do! DatabaseSession.commit transaction token
                    return int64 deleted
                })
