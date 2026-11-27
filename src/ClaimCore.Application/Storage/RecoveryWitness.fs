namespace ClaimCore.Application.Storage

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Provider identity is taken from a connected primary, independent of the last checkpoint.
type internal WitnessPrimary =
    {
        Generation: Guid
        PrimaryId: Guid
        SystemId: int64
        Timeline: string
        Flushed: string
    }

module internal WitnessPrimary =
    let configureDisclosure connection token =
        DatabaseSession.execute connection "SET LOCAL synchronous_commit = 'local'" [||] token

    let read connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    """SELECT i.generation, c.system_identifier,
                              left(pg_catalog.pg_walfile_name(pg_catalog.pg_current_wal_insert_lsn()),8),
                              pg_catalog.pg_current_wal_flush_lsn()::text, i.primary_id
                       FROM claimcore.recovery_generation i CROSS JOIN pg_catalog.pg_control_system() c
                       WHERE NOT pg_catalog.pg_is_in_recovery() AND current_setting('fsync')='on'"""
                    [||]
                    token

            match rows with
            | [ row ] ->
                return
                    {
                        Generation = DatabaseSession.value<Guid> 0 row
                        PrimaryId = DatabaseSession.value<Guid> 4 row
                        SystemId = DatabaseSession.value<int64> 1 row
                        Timeline = DatabaseSession.value<string> 2 row
                        Flushed = DatabaseSession.value<string> 3 row
                    }
            | _ ->
                return
                    raise (RecordCorruptException "Disclosure evidence requires a durable primary.")
        }

module internal WitnessCluster =
    let isIndependent mainSystemId connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT system_identifier<>$1 AND NOT pg_catalog.pg_is_in_recovery() AND current_setting('fsync')='on' FROM pg_catalog.pg_control_system()"
                    [| box mainSystemId |]
                    token

            return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
        }

    let requireIndependent mainSystemId connection token =
        task {
            let! independent = isIndependent mainSystemId connection token

            if not independent then
                raise (
                    OperatorFaultException
                        ClaimCore.Application.Contracts.OperatorFault.ConnectionFileInvalid
                )
        }

/// No case, actor, or disclosure content enters these calls.
type internal RecoveryWitness(source: NpgsqlDataSource, installationId: Guid) =
    let admission = new SemaphoreSlim(4, 4)

    let call primary sql extra (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()

            if not (admission.Wait(0)) then
                return false
            else
                use deadline = CancellationTokenSource.CreateLinkedTokenSource token
                deadline.CancelAfter(TimeSpan.FromSeconds 1.)
                let ct = deadline.Token

                try
                    try
                        return!
                            DatabaseSession.transaction
                                source
                                IsolationLevel.ReadCommitted
                                ct
                                (fun connection tx ->
                                    task {
                                        let! _ =
                                            DatabaseSession.execute
                                                connection
                                                "SET LOCAL statement_timeout='750ms'; SET LOCAL lock_timeout='750ms'; SET LOCAL synchronous_commit='local'"
                                                [||]
                                                ct

                                        let! independent =
                                            WitnessCluster.isIndependent
                                                primary.SystemId
                                                connection
                                                ct

                                        if not independent then
                                            return false
                                        else
                                            let! result =
                                                DatabaseSession.rows
                                                    connection
                                                    sql
                                                    (Array.append
                                                        [|
                                                            box installationId
                                                            box primary.Generation
                                                            box primary.SystemId
                                                            box primary.Timeline
                                                            box primary.PrimaryId
                                                        |]
                                                        extra)
                                                    ct

                                            do! DatabaseSession.commit tx ct

                                            return
                                                DatabaseSession.value<bool>
                                                    0
                                                    (List.exactlyOne result)
                                    })
                    with
                    | _ when token.IsCancellationRequested ->
                        return raise (OperationCanceledException(token))
                    | error ->
                        FailureDiagnostics.report error
                        return false
                finally
                    admission.Release() |> ignore
        }

    member _.IsActive(primary, token) =
        call primary "SELECT claimcore_witness.is_active($1,$2,$3,$4,$5)" [||] token

    member _.Acknowledge(primary, token) =
        call
            primary
            "SELECT claimcore_witness.acknowledge_disclosure($1,$2,$3,$4,$5,$6::pg_lsn)"
            [| box primary.Flushed |]
            token

    member _.AdmitDispatch(primary, date: DateOnly, token) =
        call
            primary
            "SELECT claimcore_witness.admit_dispatch($1,$2,$3,$4,$5,$6)"
            [| box date |]
            token

    interface IDisposable with
        member _.Dispose() =
            try
                source.Dispose()
            finally
                admission.Dispose()

module internal WitnessEnrollment =
    let enroll (source: NpgsqlDataSource) (witness: NpgsqlDataSource) installation token =
        task {
            let! primary, eligible, checkpoint =
                DatabaseSession.read source token (fun connection ->
                    task {
                        let! primary = WitnessPrimary.read connection token

                        let! rows =
                            DatabaseSession.rows
                                connection
                                """SELECT i.installation_id=$1 AND m.publication_generation=0
                                    AND NOT EXISTS(SELECT FROM claimcore.disclosure_trail)
                                    FROM claimcore.installation i CROSS JOIN claimcore.register_maintenance m"""
                                [| box installation |]
                                token

                        let! checkpoint = CheckpointEvidence.read connection installation token

                        return
                            primary,
                            DatabaseSession.value<bool> 0 (List.exactlyOne rows),
                            checkpoint
                    })

            if eligible then
                return!
                    DatabaseSession.transaction
                        witness
                        IsolationLevel.ReadCommitted
                        token
                        (fun connection tx ->
                            task {
                                do!
                                    WitnessCluster.requireIndependent
                                        primary.SystemId
                                        connection
                                        token

                                let! _ = WitnessPrimary.configureDisclosure connection token

                                let! _ =
                                    DatabaseSession.execute
                                        connection
                                        """INSERT INTO claimcore_witness.installations(
                                               installation,generation,system_id,timeline,primary_id,
                                               admitted_sequence,admitted_hash,expiry_floor_ticks)
                                       SELECT $1,$2,$3,$4,$5,$6,$7,$8 WHERE current_setting('fsync')='on'
                                       ON CONFLICT DO NOTHING"""
                                        [|
                                            box installation
                                            box primary.Generation
                                            box primary.SystemId
                                            box primary.Timeline
                                            box primary.PrimaryId
                                            box (Sequence.value checkpoint.Position.Sequence)
                                            box (EntryHash.toBytes checkpoint.Position.EntryHash)
                                            DatabaseSession.parameter (
                                                checkpoint.ExpiredBefore |> Option.map _.UtcTicks
                                            )
                                        |]
                                        token

                                do! DatabaseSession.commit tx token
                                return primary
                            })
            else
                return primary
        }

module internal WitnessRestore =
    type Fence =
        {
            PrimaryId: Guid
            SystemId: int64
            Timeline: string
            Required: string option
            Latest: DateOnly option
            Suppressed: DateOnly option
            Gap: bool
            AlreadyActive: bool
            Evidence: byte array option
            SealedEvidence: byte array option
            SealedApproval: byte array option
            CheckpointDigest: byte array option
            RequiredCheckpoint: InstallationCheckpoint
            Preparation: byte array option
        }

    let fence source systemId installation transition token =
        DatabaseSession.read source token (fun connection ->
            task {
                do! WitnessCluster.requireIndependent systemId connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT system_id,timeline,disclosure_lsn::text,latest_dispatch,suppress_through,
                                  (d.decision).gap_reason IS NOT NULL,
                                  i.transition IS NULL, primary_id,
                                  claimcore_witness.fence_digest(i.installation,$2),
                                  (SELECT sealed_digest FROM claimcore_witness.transitions t
                                   WHERE t.installation=i.installation AND t.transition=$2),
                                  (SELECT preparation_digest FROM claimcore_witness.preparations p
                                   WHERE p.installation=i.installation AND p.transition=$2),
                                  (SELECT sealed_approval FROM claimcore_witness.transitions t
                                   WHERE t.installation=i.installation AND t.transition=$2),
                                  (d.decision).checkpoint_digest,(d.decision).required_sequence,
                                  (d.decision).required_hash,(d.decision).required_expiry_ticks
                           FROM claimcore_witness.installations i
                           JOIN claimcore_witness.fence_decisions d ON d.installation=i.installation AND d.transition=$2
                           WHERE i.installation=$1 AND (i.transition=$2 OR (i.transition IS NULL AND generation=$2))"""
                        [| box installation; box transition |]
                        token

                return
                    match rows with
                    | [ row ] ->
                        Some
                            {
                                PrimaryId = DatabaseSession.value 7 row
                                SystemId = DatabaseSession.value 0 row
                                Timeline = DatabaseSession.value 1 row
                                Required = DatabaseSession.optional 2 row
                                Latest = DatabaseSession.optional 3 row
                                Suppressed = DatabaseSession.optional 4 row
                                Gap = DatabaseSession.value 5 row
                                AlreadyActive = DatabaseSession.value 6 row
                                Evidence = DatabaseSession.optional 8 row
                                SealedEvidence = DatabaseSession.optional 9 row
                                Preparation = DatabaseSession.optional 10 row
                                SealedApproval = DatabaseSession.optional 11 row
                                CheckpointDigest = DatabaseSession.optional 12 row
                                RequiredCheckpoint =
                                    CheckpointEvidence.fromStorage
                                        installation
                                        (DatabaseSession.value 13 row)
                                        (DatabaseSession.value 14 row)
                                        (DatabaseSession.optional 15 row)
                            }
                    | _ -> None
            })

    let private lsn (text: string) =
        let hex (value: string) =
            match
                UInt32.TryParse(
                    value,
                    Globalization.NumberStyles.AllowHexSpecifier,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, part -> Some part
            | _ -> None

        match text.Split '/' with
        | [| high; low |] ->
            match hex high, hex low with
            | Some high, Some low -> Some((uint64 high <<< 32) ||| uint64 low)
            | _ -> None
        | _ -> None

    // PostgreSQL's history file names the parent, then records its final position on the child.
    let forkPoint (timeline: string) (history: string) =
        let parent =
            UInt32.Parse(
                timeline,
                Globalization.NumberStyles.AllowHexSpecifier,
                Globalization.CultureInfo.InvariantCulture
            )

        history.Split '\n'
        |> Array.map _.Trim()
        |> Array.filter (fun line -> line.Length > 0 && not (line.StartsWith('#')))
        |> Array.tryPick (fun line ->
            let fields = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

            if fields.Length >= 2 then
                match
                    UInt32.TryParse(
                        fields[0],
                        Globalization.NumberStyles.None,
                        Globalization.CultureInfo.InvariantCulture
                    )
                with
                | true, actual when actual = parent && (lsn fields[1]).IsSome -> Some fields[1]
                | _ -> None
            else
                None)

    let provesCoverage (primary: WitnessPrimary) old fork =
        match old.Required, fork with
        | None, _ -> true
        | Some required, Some split when
            primary.SystemId = old.SystemId && primary.Timeline <> old.Timeline
            ->
            match lsn required, lsn split, lsn primary.Flushed with
            | Some needed, Some forked, Some flushed -> needed <= forked && forked <= flushed
            | _ -> false
        | _ -> false

    let widenReopenFloor source installation (primary: WitnessPrimary) (date: DateOnly) token =
        DatabaseSession.transaction source IsolationLevel.ReadCommitted token (fun connection tx ->
            task {
                do! WitnessCluster.requireIndependent primary.SystemId connection token
                let! _ = WitnessPrimary.configureDisclosure connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT CASE WHEN current_setting('fsync')='on' THEN
                               claimcore_witness.widen_reopen_floor($1,$2,$3,$4,$5,$6) ELSE false END"""
                        [|
                            box installation
                            box primary.Generation
                            box primary.PrimaryId
                            box primary.SystemId
                            box primary.Timeline
                            box date
                        |]
                        token

                do! DatabaseSession.commit tx token
                return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
            })

    let sealFence
        source
        installation
        transition
        (primary: WitnessPrimary)
        fork
        (evidence: byte array)
        (target: Guid)
        (approval: byte array)
        (checkpointDigest: byte array)
        token
        =
        DatabaseSession.transaction source IsolationLevel.ReadCommitted token (fun connection tx ->
            task {
                do! WitnessCluster.requireIndependent primary.SystemId connection token
                let! _ = WitnessPrimary.configureDisclosure connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT CASE WHEN current_setting('fsync')='on' THEN
                               claimcore_witness.seal_fence($1,$2,$3,$4,$5,$6::pg_lsn,$7::pg_lsn,$8,$9,$10)
                               ELSE false END"""
                        [|
                            box installation
                            box transition
                            box evidence
                            box primary.SystemId
                            box primary.Timeline
                            box primary.Flushed
                            DatabaseSession.parameter fork
                            box target
                            box approval
                            box checkpointDigest
                        |]
                        token

                do! DatabaseSession.commit tx token
                return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
            })

    let bindTarget
        source
        installation
        transition
        (primary: WitnessPrimary)
        (evidence: byte array)
        (preparation: byte array)
        (checkpoint: InstallationCheckpoint)
        token
        =
        DatabaseSession.transaction source IsolationLevel.ReadCommitted token (fun connection tx ->
            task {
                do! WitnessCluster.requireIndependent primary.SystemId connection token
                let! _ = WitnessPrimary.configureDisclosure connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT CASE WHEN current_setting('fsync')='on' THEN
                              claimcore_witness.prepare_target($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                              ELSE false END"""
                        [|
                            box installation
                            box transition
                            box primary.PrimaryId
                            box primary.SystemId
                            box primary.Timeline
                            box evidence
                            box preparation
                            box (Sequence.value checkpoint.Position.Sequence)
                            box (EntryHash.toBytes checkpoint.Position.EntryHash)
                            DatabaseSession.parameter (
                                checkpoint.ExpiredBefore |> Option.map _.UtcTicks
                            )
                        |]
                        token

                do! DatabaseSession.commit tx token
                return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
            })

    let activate
        source
        installation
        transition
        (primary: WitnessPrimary)
        fork
        suppression
        (preparation: byte array)
        token
        =
        DatabaseSession.transaction source IsolationLevel.ReadCommitted token (fun connection tx ->
            task {
                do! WitnessCluster.requireIndependent primary.SystemId connection token
                let! _ = WitnessPrimary.configureDisclosure connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT CASE WHEN current_setting('fsync')='on' THEN
                               claimcore_witness.activate($1,$2,$3,$4,$5::pg_lsn,$6::pg_lsn,$7,$8,$9)
                               ELSE false END"""
                        [|
                            box installation
                            box transition
                            box primary.SystemId
                            box primary.Timeline
                            box primary.Flushed
                            DatabaseSession.parameter fork
                            box suppression
                            box primary.PrimaryId
                            box preparation
                        |]
                        token

                do! DatabaseSession.commit tx token
                return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
            })
