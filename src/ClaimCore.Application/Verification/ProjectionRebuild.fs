namespace ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Owner-only maintenance. The ledger head lock excludes application writers throughout the scan.
module internal ProjectionRebuild =
    let rebuild
        (source: NpgsqlDataSource)
        masterKey
        (expected: InstallationCheckpoint)
        (handle: MaintenanceHandle)
        (token: CancellationToken)
        =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! _ = Transactions.head connection token
                    let! installation = Baseline.require connection token

                    use! cipher =
                        Baseline.loadCipher connection installation.InstallationId masterKey token

                    do! RegisterMaintenanceStore.requireActive handle connection token

                    let! scanned =
                        match
                            CheckpointEvidence.trustedInstallation
                                installation.InstallationId
                                expected
                        with
                        | Error finding -> System.Threading.Tasks.Task.FromResult(Error finding)
                        | Ok() ->
                            task {
                                let! scanned =
                                    Verification.authoritative
                                        connection
                                        installation
                                        cipher
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
                            Replay.familyRows state
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

                        let! before =
                            Verification.projections
                                connection
                                installation.InstallationId
                                state
                                token
                        // DELETE preserves old MVCC snapshots; TRUNCATE and table swapping do not.
                        for table in
                            [
                                "grants"
                                "notes"
                                "case_parties"
                                "case_links"
                                "reminders"
                                "case_work"
                                "case_payments"
                                "case_payment_totals"
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

                        for row in people |> Map.values do
                            do! PartProjections.personIdentity connection row token

                        for row in rows |> Map.values do
                            do! PartProjections.family connection row token

                        for row in people |> Map.values do
                            do! PartProjections.personGrants connection row token

                        do!
                            AttachmentStorage.rebuildProjection
                                connection
                                (Replay.attachmentProjection state)
                                token

                        let! validated =
                            Verification.projections
                                connection
                                installation.InstallationId
                                state
                                token

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
