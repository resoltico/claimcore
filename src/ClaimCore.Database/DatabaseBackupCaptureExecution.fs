namespace ClaimCore.Database

open System
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// The owner and producer communicate only through an inherited private duplex descriptor.
/// No claimant, credential, capture path or retention assertion is returned on stdout.
module internal DatabaseBackupCaptureExecution =
    let private privateRoot (setting: string) =
        let value =
            Environment.GetEnvironmentVariable setting
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Backup capture custody root is unavailable.")

        if String.IsNullOrWhiteSpace value || not (Path.IsPathFullyQualified value) then
            invalidOp "Backup capture custody root is unavailable."

        match PrivateFileService.requirePrivateDirectory value with
        | Ok() -> value
        | Error _ -> invalidOp "Backup capture custody root is not owner-private."

    let private privateSuppression () =
        let value =
            Environment.GetEnvironmentVariable "CLAIMCORE_SUPPRESSION_KEY_FILE"
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Backup capture suppression key is unavailable.")

        if String.IsNullOrWhiteSpace value then
            invalidOp "Backup capture suppression key is unavailable."

        SuppressionKeyFile.Load value

    let private now (connection: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", connection)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as instant -> instant
        | :? DateTime as instant when instant.Kind = DateTimeKind.Utc -> DateTimeOffset instant
        | _ -> invalidOp "Backup capture database clock is unavailable."

    let private writerConnection (ownerConnection: string) (witnessOwner: string) =
        let value =
            DatabaseWitnessInputs.witnessWriterConnection ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Backup capture witness writer is unavailable.")

        let primary = OwnerConnection.builder ownerConnection
        let writer = NpgsqlConnectionStringBuilder value
        let administrative = NpgsqlConnectionStringBuilder witnessOwner

        if
            writer.Username <> "claimcore_witness_writer"
            || writer.Host <> administrative.Host
            || writer.Port <> administrative.Port
            || writer.Database <> administrative.Database
            || (String.Equals(primary.Host, writer.Host, StringComparison.OrdinalIgnoreCase)
                && primary.Port = writer.Port)
        then
            invalidOp "Backup capture witness writer is not separate."

        value

    let private finishOrAbort
        (owner: NpgsqlConnection)
        (pipe: Stream)
        (session: DatabaseBackupCaptureSession)
        (token: CancellationToken)
        =
        DatabaseBackupControlPipe.allowCaptureUntil pipe session.ExpiresAt (now owner)

        let second =
            DatabaseBackupControlPipe.readFrameWithCancellation pipe token
            |> Option.defaultWith (fun () ->
                invalidOp "Backup capture FINISH or ABORT is unavailable.")

        match DatabaseBackupCaptureFrames.parse second (now owner) with
        | BackupCaptureFrame.Abort _ ->
            let aborted = session.Accept(second, now owner, token).GetAwaiter().GetResult()
            DatabaseBackupControlPipe.writeFrame pipe aborted
            false
        | BackupCaptureFrame.Finish _ ->
            let sealedReceipt =
                session.Accept(second, now owner, token).GetAwaiter().GetResult()

            DatabaseBackupControlPipe.writeFrame pipe sealedReceipt
            pipe.ReadTimeout <- 30000

            let third =
                DatabaseBackupControlPipe.readFrameWithCancellation pipe token
                |> Option.defaultWith (fun () -> invalidOp "Backup capture OBSERVE is unavailable.")

            let observed = session.Accept(third, now owner, token).GetAwaiter().GetResult()
            DatabaseBackupControlPipe.writeFrame pipe observed
            true
        | _ -> invalidOp "Backup capture frame is out of sequence."

    let private exchange
        (owner: NpgsqlConnection)
        (source: NpgsqlDataSource)
        (witnessOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        archiveRoot
        checkpointRoot
        =
        use pipe = DatabaseBackupControlPipe.openDuplex ()

        let first =
            DatabaseBackupControlPipe.readFrame pipe
            |> Option.defaultWith (fun () -> invalidOp "Backup capture BEGIN is unavailable.")

        let leaseId = Guid.NewGuid()

        let evidence =
            new DatabaseBackupCaptureEvidence(owner, witnessOwner, archiveRoot, checkpointRoot)
            :> IBackupCaptureEvidence

        use pending = new CancellationTokenSource()
        pending.CancelAfter(TimeSpan.FromMinutes(30.))
        let token = pending.Token
        let started = now owner

        let session, held =
            DatabaseBackupCaptureSession.Begin(
                owner,
                source,
                witness,
                commitments,
                evidence,
                leaseId,
                first,
                started,
                token
            )
            |> fun work -> work.GetAwaiter().GetResult()

        use capture = session :> IDisposable
        DatabaseBackupControlPipe.writeFrame pipe held
        finishOrAbort owner pipe session token

    let private withOwner (ownerConnection: string) action =
        let app, _, witnessOwnerConnection =
            DatabaseWriterHandoffAbortExecution.sourceInput ownerConnection
            |> Result.defaultWith (fun _ -> invalidOp "Backup capture sources are unavailable.")

        let writer = writerConnection ownerConnection witnessOwnerConnection
        let archiveRoot = privateRoot "CLAIMCORE_BACKUP_ARCHIVE_ROOT"
        let checkpointRoot = privateRoot "CLAIMCORE_BACKUP_CHECKPOINT_ROOT"
        use suppression = privateSuppression ()

        use custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Backup capture witness keys are unavailable.")

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Backup capture writer capability is unavailable.")

        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        OwnerConnection.requireIdentity owner
        SchemaBaseline.requireCurrent owner
        let identity, keyId, check = DatabaseVerifyData.identity owner
        let commitments = DatabaseVerifyData.commitments suppression identity keyId check
        let store = capability.Use(fun material -> new Store(writer, identity, material))
        use witness = new WitnessProtocol(store, custody, identity)
        witness.Admit()
        use source = (new NpgsqlDataSourceBuilder(app)).Build()
        use witnessOwner = new NpgsqlConnection(witnessOwnerConnection)
        witnessOwner.Open()
        action owner source witnessOwner witness commitments archiveRoot checkpointRoot

    let run (ownerConnection: string) =
        try
            if withOwner ownerConnection exchange then
                AdministrationOutcome.Completed None
            else
                AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        with _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let reconcile (ownerConnection: string) (leaseId: Guid) =
        try
            let archive = privateRoot "CLAIMCORE_BACKUP_ARCHIVE_ROOT"
            let checkpoint = privateRoot "CLAIMCORE_BACKUP_CHECKPOINT_ROOT"

            let audit =
                DatabaseWitnessInputs.witnessAuditConnection ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Backup capture witness audit is unavailable.")

            let primary = OwnerConnection.builder ownerConnection
            let witnessSource = NpgsqlConnectionStringBuilder audit

            if
                witnessSource.Username <> "claimcore_witness_auditor"
                || (String.Equals(
                       primary.Host,
                       witnessSource.Host,
                       StringComparison.OrdinalIgnoreCase
                    )
                    && primary.Port = witnessSource.Port)
            then
                invalidOp "Backup capture readback witness is not separate."

            use custody =
                DatabaseWitnessInputs.keyRing ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Backup capture witness keys are unavailable.")

            use owner = new NpgsqlConnection(primary.ConnectionString)
            owner.Open()
            OwnerConnection.requireIdentity owner
            SchemaBaseline.requireCurrent owner
            let identity, _, _ = DatabaseVerifyData.identity owner

            use witness =
                new WitnessProtocol(Store.OpenAudit(audit, identity), custody, identity)

            witness.AdmitReadOnly()

            DatabaseBackupCaptureReconciliation.inspect owner witness archive checkpoint leaseId
            |> ignore

            AdministrationOutcome.Completed None
        with _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
