namespace ClaimCore.Application.Storage

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Json
open ClaimCore.RecordFormat
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// The operator isolates ALL client traffic before calling this owner-only boundary.
/// Maintenance is checked here; no database query can prove external traffic isolation.
module internal RestoreAdmission =
    let private notAdmitted () =
        raise (OperatorFaultException OperatorFault.StaleRecoveryPreparation)

    let private historyEvidence history (primary: WitnessPrimary) connection token =
        task {
            match history with
            | None -> return None
            | Some(name, supplied) ->
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore_restore_provider.current_timeline_history()"
                        [||]
                        token

                match DatabaseSession.optional<string> 0 (List.exactlyOne rows) with
                | Some actual when
                    name = primary.Timeline + ".history"
                    && Encoding.UTF8.GetByteCount actual <= 65536
                    && actual = supplied
                    ->
                    return Some(SHA256.HashData(Encoding.UTF8.GetBytes actual))
                | _ -> return notAdmitted ()
        }

    let private samePrimary (left: WitnessPrimary) (right: WitnessPrimary) =
        left.Generation = right.Generation
        && left.PrimaryId = right.PrimaryId
        && left.SystemId = right.SystemId
        && left.Timeline = right.Timeline

    [<NoEquality; NoComparison>]
    type private Approval =
        {
            Before: WitnessPrimary
            Fence: byte array
            Archive: byte array option
            History: (string * string) option
            RequiredCheckpoint: InstallationCheckpoint
            SealDecision: Guid -> System.Threading.Tasks.Task<bool>
            CredentialClock:
                unit -> System.Threading.Tasks.Task<(DateTimeOffset * DateTimeOffset option) option>
        }

    let private requireRetainedCheckpoint
        connection
        (required: InstallationCheckpoint)
        cutoff
        token
        =
        task {
            let! retained = CheckpointEvidence.retainsPosition connection required.Position token

            if not retained || cutoff < required.ExpiredBefore then
                notAdmitted ()
        }

    let private approvalDigest
        (target: Guid)
        (primary: WitnessPrimary)
        (archive: byte array option)
        trusted
        =
        JsonValue.Array
            [
                JsonValue.String "claimcore/restore-approval"
                JsonValue.String(target.ToString("D"))
                JsonValue.String(primary.SystemId.ToString(CultureInfo.InvariantCulture))
                JsonValue.String primary.Timeline
                archive
                |> Option.map (Convert.ToHexStringLower >> JsonValue.String)
                |> Option.defaultValue JsonValue.Null
                CheckpointRecord.codec.Write trusted
            ]
        |> CanonicalWriter.bytes
        |> SHA256.HashData

    type private Preparation =
        {
            TargetId: Guid
            SystemId: int64
            Timeline: string
            Fence: byte array
            Archive: byte array option
            Approved: InstallationCheckpoint
            Report: RestoreAdmissionReport
            PublicationGeneration: int64
            CredentialFloor: DateTimeOffset
        }

    let private checkpoint (bytes: byte array) =
        match CheckpointRecord.decode bytes with
        | Ok value when CheckpointRecord.encode value = bytes -> value
        | _ -> raise (RecordCorruptException "Invalid stored restore checkpoint.")

    let private readPreparation connection transition token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    """SELECT primary_id,system_id,timeline,fence_digest,history_digest,
                              approved_checkpoint,admitted_checkpoint,invalidated_sessions,publication_generation,credential_floor
                       FROM claimcore.restore_preparations WHERE generation=$1"""
                    [| box transition |]
                    token

            return
                rows
                |> List.tryExactlyOne
                |> Option.map (fun row ->
                    {
                        TargetId = DatabaseSession.value 0 row
                        SystemId = DatabaseSession.value 1 row
                        Timeline = DatabaseSession.value 2 row
                        Fence = DatabaseSession.value 3 row
                        Archive = DatabaseSession.optional 4 row
                        Approved = DatabaseSession.value 5 row |> checkpoint
                        Report =
                            {
                                Checkpoint = DatabaseSession.value 6 row |> checkpoint
                                InvalidatedSessions = DatabaseSession.value 7 row
                            }
                        PublicationGeneration = DatabaseSession.value 8 row
                        CredentialFloor = DatabaseSession.instant 9 row
                    })
        }

    let private writePreparation
        connection
        generation
        primary
        (approval: Approval)
        trusted
        report
        publicationGeneration
        token
        =
        DatabaseSession.execute
            connection
            """INSERT INTO claimcore.restore_preparations(
                   generation,primary_id,system_id,timeline,fence_digest,history_digest,
                   approved_checkpoint,admitted_checkpoint,invalidated_sessions,publication_generation,credential_floor)
               VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)"""
            [|
                box generation
                box primary.PrimaryId
                box primary.SystemId
                box primary.Timeline
                box approval.Fence
                DatabaseSession.parameter approval.Archive
                box (CheckpointRecord.encode trusted)
                box (CheckpointRecord.encode report.Checkpoint)
                box report.InvalidatedSessions
                box publicationGeneration
                box (Option.get primary.CredentialFloor)
            |]
            token

    let private commitment (transition: Guid) (preparation: Preparation) =
        let text = JsonValue.String
        let bytes (value: byte array) = text (Convert.ToHexStringLower value)

        let scalar (value: int64) =
            text (value.ToString(CultureInfo.InvariantCulture))

        JsonValue.Array
            [
                text "claimcore/restore-preparation"
                text (transition.ToString("D"))
                text (preparation.TargetId.ToString("D"))
                scalar preparation.SystemId
                text preparation.Timeline
                bytes preparation.Fence
                preparation.Archive |> Option.map bytes |> Option.defaultValue JsonValue.Null
                preparation.Approved |> CheckpointRecord.codec.Write
                preparation.Report.Checkpoint |> CheckpointRecord.codec.Write
                scalar preparation.Report.InvalidatedSessions
                scalar preparation.PublicationGeneration
                scalar preparation.CredentialFloor.UtcTicks
            ]
        |> CanonicalWriter.bytes
        |> SHA256.HashData

    let private admitTarget
        (approval: Approval option)
        recoveryGeneration
        (source: NpgsqlDataSource)
        masterKey
        (trusted: InstallationCheckpoint)
        (handle: MaintenanceHandle)
        (token: CancellationToken)
        =
        task {
            let! prepared =
                RegisterMaintenanceStore.prepare
                    source
                    masterKey
                    handle
                    token
                    (fun connection publication cipher ->
                        task {
                            let installation = publication.Installation

                            match
                                CheckpointEvidence.trustedInstallation
                                    installation.InstallationId
                                    trusted
                            with
                            | Error finding -> return Error finding
                            | Ok() ->
                                let! history =
                                    Verification.authoritative
                                        connection
                                        installation
                                        cipher
                                        (Some trusted.Position)
                                        token

                                match history with
                                | Error finding -> return Error finding
                                | Ok _ when approval.IsNone -> return Ok publication
                                | Ok state ->
                                    let! verified =
                                        Verification.projections
                                            connection
                                            installation.InstallationId
                                            (Replay.familyRows state)
                                            (Replay.peopleRows state)
                                            state
                                            token

                                    return verified |> Result.map (fun () -> publication)
                        })

            match prepared with
            | Error finding -> return Error finding
            | Ok publication ->
                let validateTarget (approved: Approval) connection =
                    task {
                        let! current = WitnessPrimary.read connection token
                        let! archive = historyEvidence approved.History current connection token

                        if
                            not (samePrimary approved.Before current) || archive <> approved.Archive
                        then
                            notAdmitted ()

                        let! local = ExpiryFrontierStorage.read connection token

                        do!
                            requireRetainedCheckpoint
                                connection
                                approved.RequiredCheckpoint
                                (max local trusted.ExpiredBefore)
                                token
                    }

                let! target =
                    task {
                        match approval, recoveryGeneration with
                        | Some approved, Some generation ->
                            let! reserved =
                                RegisterMaintenanceStore.reserveRestoreTarget
                                    source
                                    masterKey
                                    publication
                                    generation
                                    token
                                    (validateTarget approved)

                            let! sealedDecision = approved.SealDecision reserved

                            if not sealedDecision then
                                notAdmitted ()

                            return Some reserved
                        | _ -> return None
                    }

                let! independentFloor =
                    task {
                        match approval with
                        | None -> return None
                        | Some approved ->
                            let start = TimeProvider.System.GetUtcNow()
                            let! evidence = approved.CredentialClock()
                            let finish = TimeProvider.System.GetUtcNow()

                            match evidence with
                            | Some(observed, prior) when
                                finish >= start
                                && observed >= start - CredentialFreshness.maximumLead
                                && observed <= finish + CredentialFreshness.maximumLead
                                ->
                                return Some(max finish (prior |> Option.fold max observed))
                            | _ -> return notAdmitted ()
                    }

                return!
                    RegisterMaintenanceStore.publish
                        source
                        masterKey
                        publication
                        token
                        (fun connection _ ->
                            task {
                                match approval with
                                | None -> ()
                                | Some approved -> do! validateTarget approved connection

                                match recoveryGeneration with
                                | Some generation ->
                                    let! _ =
                                        Baseline.rotateEncryptionGeneration
                                            connection
                                            (Some generation)
                                            token

                                    let! _ = SessionRows.advanceOrder connection token
                                    let! local = SessionRows.now connection token
                                    let host = TimeProvider.System.GetUtcNow()

                                    if
                                        (local - host).Duration() > CredentialFreshness.maximumLead
                                    then
                                        notAdmitted ()

                                    let cutoff =
                                        independentFloor |> Option.fold max (max local host)

                                    let! _ =
                                        DatabaseSession.execute
                                            connection
                                            "UPDATE claimcore.recovery_generation SET generation=$1, primary_id=coalesce($2::uuid,gen_random_uuid()), reopen_pending=$3, credential_floor=greatest(credential_floor,$4) WHERE singleton"
                                            [|
                                                box generation
                                                DatabaseSession.parameter target
                                                box approval.IsSome
                                                box cutoff
                                            |]
                                            token

                                    let! _ =
                                        DatabaseSession.execute
                                            connection
                                            "DELETE FROM claimcore.browser_flows; DELETE FROM claimcore.browser_session_cuts"
                                            [||]
                                            token

                                    ()
                                | None -> ()

                                match trusted.ExpiredBefore with
                                | Some cutoff ->
                                    let! _ = ExpiryFrontierStorage.advance connection cutoff token
                                    ()
                                | None -> ()

                                let! removed =
                                    DatabaseSession.execute
                                        connection
                                        "DELETE FROM claimcore.sessions"
                                        [||]
                                        token

                                let! checkpoint =
                                    CheckpointEvidence.read
                                        connection
                                        publication.Installation.InstallationId
                                        token

                                let report =
                                    {
                                        Checkpoint = checkpoint
                                        InvalidatedSessions = int64 removed
                                    }

                                match approval, recoveryGeneration with
                                | Some approved, Some generation ->
                                    let! primary = WitnessPrimary.read connection token

                                    let! _ =
                                        writePreparation
                                            connection
                                            generation
                                            primary
                                            approved
                                            trusted
                                            report
                                            (publication.Generation + 1L)
                                            token

                                    ()
                                | _ -> ()

                                return Ok report
                            })
        }

    let admit source masterKey trusted handle token =
        task {
            let generation = Guid.NewGuid()
            return! admitTarget None (Some generation) source masterKey trusted handle token
        }

    let admitWitnessed
        source
        masterKey
        (trusted: InstallationCheckpoint)
        handle
        (witness: NpgsqlDataSource)
        transition
        history
        token
        =
        let trustedDigest = CheckpointRecord.encode trusted |> SHA256.HashData

        let snapshot connection (cipher: InstallationCipher) =
            task {
                let! primary = WitnessPrimary.read connection token
                let! archive = historyEvidence history primary connection token
                let! preparation = readPreparation connection transition token
                return primary, archive, preparation, cipher.Generation
            }

        let observe () =
            RegisterMaintenanceStore.prepare
                source
                masterKey
                handle
                token
                (fun connection publication cipher ->
                    task {
                        let! current = snapshot connection cipher
                        return publication, current
                    })

        let checkPrepared
            (publication: RegisterMaintenanceStore.Publication)
            primary
            archive
            encryption
            preparation
            =
            match preparation with
            | Some saved when
                primary.Generation = transition
                && primary.PrimaryId = saved.TargetId
                && primary.SystemId = saved.SystemId
                && primary.Timeline = saved.Timeline
                && encryption = transition
                && saved.Approved = trusted
                && saved.Archive = archive
                && saved.Report.Checkpoint = publication.Checkpoint
                && saved.PublicationGeneration = publication.Generation
                && primary.CredentialFloor = Some saved.CredentialFloor
                ->
                saved
            | _ -> notAdmitted ()

        let finish (fence: WitnessRestore.Fence) fork =
            task {
                let! publication, _ = observe ()

                let! current, saved, businessDate =
                    RegisterMaintenanceStore.confirmWith
                        source
                        masterKey
                        publication
                        token
                        (fun connection cipher ->
                            task {
                                let! primary, archive, stored, encryption =
                                    snapshot connection cipher

                                let saved =
                                    checkPrepared publication primary archive encryption stored

                                do!
                                    requireRetainedCheckpoint
                                        connection
                                        fence.RequiredCheckpoint
                                        saved.Report.Checkpoint.ExpiredBefore
                                        token

                                if not fence.AlreadyActive then
                                    let! sessions =
                                        DatabaseSession.rows
                                            connection
                                            "SELECT count(*) FROM claimcore.sessions"
                                            [||]
                                            token

                                    if
                                        DatabaseSession.value<int64> 0 (List.exactlyOne sessions)
                                        <> 0L
                                    then
                                        notAdmitted ()

                                let! rows =
                                    DatabaseSession.rows
                                        connection
                                        "SELECT clock_timestamp()"
                                        [||]
                                        token

                                let calendar =
                                    InstallationCheck.calendar
                                        publication.Installation.BusinessTimeZone

                                return
                                    primary,
                                    saved,
                                    calendar.DateAt(
                                        DatabaseSession.instant 0 (List.exactlyOne rows)
                                    )
                            })

                let seal = commitment transition saved
                let installation = publication.Installation.InstallationId
                let approved = approvalDigest current.PrimaryId current saved.Archive trusted

                if
                    fence.CheckpointDigest <> Some trustedDigest
                    || fence.SealedApproval <> Some approved
                then
                    notAdmitted ()

                if fence.AlreadyActive then
                    if
                        current.PrimaryId <> fence.PrimaryId
                        || current.SystemId <> fence.SystemId
                        || current.Timeline <> fence.Timeline
                        || fence.Preparation <> Some seal
                        || fence.CredentialFloor <> current.CredentialFloor
                    then
                        return notAdmitted ()
                    else
                        return Ok saved.Report
                elif
                    fence.SealedEvidence <> Some saved.Fence
                    || not (fence.Gap || WitnessRestore.provesCoverage current fence fork)
                then
                    return notAdmitted ()
                else
                    let! bound =
                        WitnessRestore.bindTarget
                            witness
                            installation
                            transition
                            current
                            saved.Fence
                            seal
                            saved.Report.Checkpoint
                            saved.CredentialFloor
                            token

                    if not bound then
                        notAdmitted ()

                    let suppression =
                        [
                            yield businessDate
                            yield! Option.toList fence.Latest
                            yield! Option.toList fence.Suppressed
                        ]
                        |> List.max

                    let! activated =
                        WitnessRestore.activate
                            witness
                            installation
                            transition
                            current
                            fork
                            suppression
                            seal
                            token

                    return if activated then Ok saved.Report else notAdmitted ()
            }

        task {
            let! initial, (primary, archive, prepared, encryption) = observe ()

            // A transition already staged on this target has an immutable approval.
            if prepared.IsSome || primary.Generation = transition then
                checkPrepared initial primary archive encryption prepared |> ignore

            let installation = initial.Installation.InstallationId

            let! selected =
                WitnessRestore.fence witness primary.SystemId installation transition token

            match selected with
            | None -> return notAdmitted ()
            | Some { CheckpointDigest = Some expected } when expected <> trustedDigest ->
                return notAdmitted ()
            | Some fence ->
                let fork = history |> Option.bind (snd >> WitnessRestore.forkPoint fence.Timeline)

                if fence.AlreadyActive || prepared.IsSome then
                    return! finish fence fork
                elif not (fence.Gap || WitnessRestore.provesCoverage primary fence fork) then
                    return notAdmitted ()
                else
                    let evidence = fence.Evidence |> Option.defaultWith notAdmitted

                    let approved =
                        {
                            Before = primary
                            Fence = evidence
                            Archive = archive
                            History = history
                            RequiredCheckpoint = fence.RequiredCheckpoint
                            CredentialClock =
                                fun () ->
                                    WitnessRestore.credentialClock
                                        witness
                                        primary.SystemId
                                        installation
                                        transition
                                        token
                            SealDecision =
                                fun target ->
                                    WitnessRestore.sealFence
                                        witness
                                        installation
                                        transition
                                        primary
                                        fork
                                        evidence
                                        target
                                        (approvalDigest target primary archive trusted)
                                        trustedDigest
                                        token
                        }

                    let! result =
                        admitTarget
                            (Some approved)
                            (Some transition)
                            source
                            masterKey
                            trusted
                            handle
                            token

                    match result with
                    | Error finding -> return Error finding
                    | Ok _ ->
                        let! sealedFence =
                            WitnessRestore.fence
                                witness
                                primary.SystemId
                                installation
                                transition
                                token

                        return! finish (sealedFence |> Option.defaultWith notAdmitted) fork
        }


    /// Pin the actual reopen date before dropping local maintenance. If the clock crosses
    /// a business-date boundary before the head is acquired, keep the pause and retry.
    let resume source masterKey (witness: NpgsqlDataSource) handle token =
        task {
            let! reopen =
                DatabaseSession.read source token (fun connection ->
                    task {
                        do! OwnerAdmission.require connection token
                        let! installation = Baseline.require connection token
                        let! pending = RegisterMaintenanceStore.reopenPending connection token
                        let! primary = WitnessPrimary.read connection token

                        do!
                            DatabaseSession.read witness token (fun target ->
                                WitnessCluster.requireIndependent primary.SystemId target token)

                        if pending then
                            do! RegisterMaintenanceStore.requireActive handle connection token

                            let! current =
                                DatabaseSession.rows
                                    connection
                                    "SELECT clock_timestamp()"
                                    [||]
                                    token

                            let calendar = InstallationCheck.calendar installation.BusinessTimeZone

                            let date =
                                calendar.DateAt(
                                    DatabaseSession.instant 0 (List.exactlyOne current)
                                )

                            return Some(installation.InstallationId, primary, date)
                        else
                            return None
                    })

            match reopen with
            | None ->
                return!
                    RegisterMaintenanceStore.change
                        source
                        masterKey
                        (MaintenanceChange.Resume handle)
                        token
            | Some(installation, primary, date) ->
                let! protectedDate =
                    WitnessRestore.widenReopenFloor witness installation primary date token

                if not protectedDate then
                    return raise (OperatorFaultException OperatorFault.StaleRecoveryPreparation)
                else
                    return!
                        RegisterMaintenanceStore.changeForReopen
                            source
                            masterKey
                            handle
                            (primary.Generation, primary.PrimaryId, date)
                            token
        }
