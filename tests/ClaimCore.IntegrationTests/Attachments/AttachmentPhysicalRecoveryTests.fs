module ClaimCore.IntegrationTests.AttachmentPhysicalRecoveryTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

module Attachments = AttachmentPostgresTests

let private storageDirectory = "/var/lib/postgresql"

let private runPostgres (database: PostgresFixture.Database) (arguments: string list) =
    task {
        let! result =
            database.Container.ExecAsync(
                List.toArray ("runuser" :: "-u" :: "postgres" :: "--" :: arguments),
                token
            )

        if result.ExitCode <> Nullable 0L then
            failtestf
                "Physical recovery fixture command %s failed (%O): %s"
                arguments.Head
                result.ExitCode
                result.Stderr
    }

let private takeBase database =
    runPostgres
        database
        [
            "pg_basebackup"
            "-h"
            "/var/run/postgresql"
            "-U"
            "postgres"
            "-D"
            storageDirectory + "/attachment-base"
            "-X"
            "stream"
            "--checkpoint=fast"
        ]

let private archivePoint (database: PostgresFixture.Database) label =
    task {
        // WAL archive orchestration belongs to the cluster operator in this fixture.
        use! sql = database.SuperuserSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                sql
                "SELECT pg_walfile_name(pg_create_restore_point($1))"
                [| box label |]
                token

        let wal = List.exactlyOne rows |> DatabaseSession.value<string> 0
        let! _ = DatabaseSession.rows sql "SELECT pg_switch_wal()" [||] token

        do!
            runPostgres
                database
                [
                    "sh"
                    "-ceu"
                    """
                for seconds in $(seq 1 45); do
                  if test -f "/var/lib/postgresql/attachment-wal/$1"; then exit 0; fi
                  sleep 1
                done
                exit 1
                """
                    "--"
                    wal
                ]
    }

let private promote database point port =
    runPostgres
        database
        [
            "sh"
            "-ceu"
            """
        data="/var/lib/postgresql/attachment-restore-$2"
        cp -a /var/lib/postgresql/attachment-base "$data"
        touch "$data/recovery.signal"
        printf '%s\n' \
          "port = $2" \
          "listen_addresses = '*'" \
          "hot_standby = on" \
          "archive_mode = off" \
          "restore_command = 'cp /var/lib/postgresql/attachment-wal/%f %p'" \
          "recovery_target_name = '$1'" \
          "recovery_target_action = 'promote'" >> "$data/postgresql.auto.conf"
        pg_ctl -w -t 45 -D "$data" -l "$data/recovery.log" start
        for seconds in $(seq 1 45); do
          if [ "$(psql -X -At -p "$2" -d postgres -c 'SELECT NOT pg_is_in_recovery()' 2>/dev/null || true)" = t ]; then
            exit 0
          fi
          sleep 1
        done
        tail -n 30 "$data/recovery.log" >&2
        exit 1
        """
            "--"
            point
            string port
        ]

let private endpoint (database: PostgresFixture.Database) (port: int) (connectionString: string) =
    NpgsqlConnectionStringBuilder(
        connectionString,
        Port = int (database.Container.GetMappedPublicPort port)
    )
        .ConnectionString

let private restoreCases (database: PostgresFixture.Database) port expected newer =
    task {
        use administrative =
            NpgsqlDataSource.Create(
                endpoint database port (PostgresFixture.ownerConnectionString database)
            )

        use runtime =
            NpgsqlDataSource.Create(
                endpoint database port (PostgresFixture.runtimeConnectionString database)
            )

        let modeId = Guid.Parse "10000000-0000-4000-8000-000000000177"

        let! maintenance =
            RegisterMaintenanceStore.change
                administrative
                database.MasterKey
                (MaintenanceChange.Acquire(0L, modeId))
                token

        Expect.isTrue
            maintenance.State.Active
            "Recovered clusters stay in maintenance during admission"

        let handle = MaintenanceHandle.create 1L modeId |> Option.get

        match newer with
        | Some later ->
            let! refused =
                RestoreAdmission.admit administrative database.MasterKey later handle token

            match refused with
            | Error failure ->
                Expect.equal
                    failure.Finding
                    VerificationFinding.ExpectedPosition
                    "The earlier physical restore cannot cross the later trusted checkpoint"
            | Ok _ ->
                failtest "The earlier restore admitted a checkpoint containing a later erasure"
        | None -> ()

        let! admitted =
            RestoreAdmission.admit administrative database.MasterKey expected handle token

        Expect.isOk
            admitted
            "The isolated promoted cluster admits its independent matching checkpoint"

        let! verified =
            Verification.verify administrative database.MasterKey (Some expected) token

        Expect.equal
            (position verified)
            expected.Position
            "Recovered history and authoritative bytes verify against the external checkpoint"

        let fixture =
            { database.Fixture with
                Store =
                    LedgerStore(
                        runtime,
                        database.Installation,
                        database.Cipher,
                        Transactions.timeouts
                    )
                    :> IStore
            }

        let actor = core fixture fixture.Owner
        let! original = Attachments.status actor (id fixture 21)
        let reattached = Attachments.attachmentGuid fixture 23
        let! current = Attachments.download actor reattached

        match newer with
        | Some _ ->
            Expect.equal
                original
                AttachmentDisclosure.Withheld
                "The withdrawn ID stays withheld after WAL recovery"

            match current with
            | Outcome.Succeeded content ->
                Expect.sequenceEqual
                    ((VerifiedAttachment.bytes content.Bytes).ToArray())
                    Attachments.bytes
                    "The reattached file still has the exact committed bytes"
            | other -> failtestf "Expected restored attachment bytes: %A" other
        | None ->
            Expect.equal
                original
                AttachmentDisclosure.Erased
                "The later restore keeps structural acceptance with erased disclosure"

            match current with
            | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
            | other -> failtestf "The later restore discloses an erased attachment: %A" other

            use! sql = administrative.OpenConnectionAsync()

            let! rows =
                DatabaseSession.rows
                    sql
                    """
                    SELECT (SELECT count(*) FROM claimcore.attachment_blobs WHERE payload IS NOT NULL OR digest IS NOT NULL OR length IS NOT NULL),
                           (SELECT count(*) FROM claimcore.attachment_references WHERE blob_id IS NOT NULL),
                           (SELECT count(*) FROM claimcore.case_attachments)
                    """
                    [||]
                    token

            let row = List.exactlyOne rows

            for index in [ 0; 1; 2 ] do
                Expect.equal
                    (DatabaseSession.value<int64> index row)
                    0L
                    "The later physical recovery has no live attachment descriptor, payload or projection"
    }

let private qualify database =
    task {
        do! takeBase database
        let! actor = Attachments.setup database
        let fixture = database.Fixture
        let original = Attachments.attachmentGuid fixture 21

        let! added =
            Attachments.upload
                actor
                (Attachments.add fixture 21 "pre-erasure.txt")
                Attachments.bytes

        accepted added |> ignore

        let! withdrawn =
            Service.executeSubmission actor (Attachments.withdraw fixture 22 original) token

        accepted withdrawn |> ignore

        let! reattached =
            Attachments.upload actor (Attachments.add fixture 23 "reattached.txt") Attachments.bytes

        accepted reattached |> ignore

        let! before = checkpoint database
        do! archivePoint database "attachment_before_erasure"

        let! erased = Service.executeSubmission actor (Attachments.erase fixture 24) token
        accepted erased |> ignore

        let! after = checkpoint database
        do! archivePoint database "attachment_after_erasure"

        do! promote database "attachment_before_erasure" 5433
        do! promote database "attachment_after_erasure" 5434

        do! restoreCases database 5433 before (Some after)
        do! restoreCases database 5434 after None
    }

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL attachment physical base backup and WAL recovery before and after erasure"
        (async {
            let! database = PostgresFixture.createArchived () |> Async.AwaitTask

            try
                do! qualify database |> Async.AwaitTask
            finally
                database.Fixture.Dispose()
        })

let private history (database: PostgresFixture.Database) port (primary: WitnessPrimary) =
    task {
        let filename = primary.Timeline + ".history"

        let! result =
            database.Container.ExecAsync(
                [|
                    "runuser"
                    "-u"
                    "postgres"
                    "--"
                    "cat"
                    $"/var/lib/postgresql/attachment-restore-{port}/pg_wal/{filename}"
                |],
                token
            )

        Expect.equal result.ExitCode (Nullable 0L) "Read the actual promotion history"
        return Some(filename, result.Stdout)
    }

let private loseCommit label (connectionString: string) number operation =
    task {
        let settings = NpgsqlConnectionStringBuilder connectionString

        use proxy =
            new CommitProxy.Proxy(
                nonNull settings.Host,
                settings.Port,
                CommitProxy.Drop.AfterCommit,
                commitNumber = number,
                allowReconnect = true
            )

        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        use source = NpgsqlDataSource.Create(settings.ConnectionString)
        let mutable unknown = false

        try
            let! _ = operation source
            ()
        with :? AdministrationOutcomeUnknownException ->
            unknown <- true

        Expect.isTrue proxy.Intercepted.IsCompleted ("Dropped a real acknowledgement for " + label)
        Expect.isTrue unknown ("Reported an uncertain outcome for " + label)
    }

let private terminateAtCommit
    label
    (connectionString: string)
    number
    (start: string -> ProcessStartInfo)
    =
    task {
        let settings = NpgsqlConnectionStringBuilder connectionString

        use proxy =
            new CommitProxy.Proxy(
                nonNull settings.Host,
                settings.Port,
                CommitProxy.Drop.HoldAfterCommit,
                commitNumber = number,
                allowReconnect = true
            )

        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        let config = start settings.ConnectionString
        use child = Process.Start config |> nonNull
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()

        try
            let exited = child.WaitForExitAsync()

            let! completed =
                Task.WhenAny(proxy.Intercepted, exited).WaitAsync(TimeSpan.FromSeconds 30.)

            if not (Object.ReferenceEquals(completed, proxy.Intercepted)) then
                let! stdout = output
                let! stderr = errors

                failtestf
                    "%s exited before its committed reply could be held (%d): %s %s"
                    label
                    child.ExitCode
                    stdout
                    stderr

            Expect.isFalse child.HasExited ("The real owner process is still waiting for " + label)
            child.Kill(true)
            do! exited.WaitAsync(TimeSpan.FromSeconds 10.)
            Expect.notEqual child.ExitCode 0 ("Forced process exit at " + label)
        finally
            if not child.HasExited then
                child.Kill(true)
    }

let private ownerSql (witnessDatabase: Testcontainers.PostgreSql.PostgreSqlContainer) statement =
    witnessDatabase.ExecAsync(
        [|
            "psql"
            "-XwqAt"
            "-U"
            "postgres"
            "-d"
            "postgres"
            "-v"
            "ON_ERROR_STOP=1"
            "-c"
            "SET ROLE claimcore_witness_owner; SET synchronous_commit=local; " + statement
        |],
        token
    )

let private recordGap witnessDatabase (installation: Guid) (transition: Guid) reason =
    ownerSql
        witnessDatabase
        $"SELECT claimcore_witness.record_gap('{installation:D}','{transition:D}','{reason}');"

let private supersedeTarget
    witnessDatabase
    (installation: Guid)
    (expected: Guid)
    (replacement: Guid)
    =
    ownerSql
        witnessDatabase
        $"SELECT claimcore_witness.supersede_lost_target('{installation:D}','{expected:D}','{replacement:D}');"

let private probeGapAndProcessKills
    (database: PostgresFixture.Database)
    (witnessDatabase: Testcontainers.PostgreSql.PostgreSqlContainer)
    (witness: RecoveryWitness)
    witnessOwner
    witnessConnection
    (trusted: InstallationCheckpoint)
    (previous: WitnessPrimary)
    (early: NpgsqlDataSource)
    (late: NpgsqlDataSource)
    handle
    =
    task {
        let directory = PrivateTestPaths.newPrivateDirectory "claimcore-restore-process-"
        let mutable passed = false

        try
            let transition = Guid.NewGuid()
            let installation = trusted.InstallationId

            let main port =
                endpoint database port (PostgresFixture.ownerConnectionString database)

            let privateText = AdministrationProcess.privateText directory
            let approvedCutoff = trusted.ExpiredBefore.Value.AddMinutes(1.)

            let withCutoff position cutoff =
                InstallationCheckpoint.create installation position (Some cutoff) |> Option.get

            let strongerCheckpoint = withCutoff trusted.Position approvedCutoff

            let checkpoint =
                privateText
                    "checkpoint.json"
                    (Text.Encoding.UTF8.GetString(
                        ClaimCore.RecordFormat.CheckpointRecord.encode strongerCheckpoint
                    ))

            let witnessFile = privateText "witness.connection" witnessConnection
            let registerFile = privateText "register.connection" (main 5433)

            let configuration =
                typeof<ClaimCore.Server.Administration.DatabaseSuccess>
                    .Assembly.GetCustomAttributes(
                        typeof<Reflection.AssemblyConfigurationAttribute>,
                        false
                    )
                |> Array.exactlyOne
                |> unbox<Reflection.AssemblyConfigurationAttribute>
                |> _.Configuration

            let! binary =
                AdministrationProcess.publishConfiguration
                    configuration
                    (IO.Path.Combine(directory, "published"))

            // Separate native process for the documented SQL fence. The connection
            // and all operation inputs use private files or the owned process environment.
            let script =
                [
                    sprintf "#r @\"%s\"" typeof<NpgsqlConnection>.Assembly.Location
                    sprintf
                        "#r @\"%s\""
                        typeof<Microsoft.Extensions.Logging.ILogger>.Assembly.Location
                    "open System"
                    "open System.IO"
                    "open Npgsql"
                    "let fence () ="
                    "    use connection = new NpgsqlConnection(File.ReadAllText(Environment.GetEnvironmentVariable \"CLAIMCORE_TEST_CONNECTION\"))"
                    "    connection.Open()"
                    "    use transaction = connection.BeginTransaction()"
                    "    use command = new NpgsqlCommand(\"SELECT claimcore_witness.fence($1,$2,$3)\", connection, transaction)"
                    "    for name in [ \"CLAIMCORE_TEST_INSTALLATION\"; \"CLAIMCORE_TEST_GENERATION\"; \"CLAIMCORE_TEST_TRANSITION\" ] do"
                    "        command.Parameters.Add(NpgsqlParameter(Value = Guid.Parse(Environment.GetEnvironmentVariable name))) |> ignore"
                    "    if not (unbox<bool>(command.ExecuteScalar())) then failwith \"Fence refused\""
                    "    transaction.Commit()"
                    "fence ()"
                ]
                |> String.concat "\n"
                |> privateText "fence.fsx"

            do!
                terminateAtCommit "external fence" witnessConnection 1 (fun proxied ->
                    let config =
                        AdministrationProcess.start [ "fsi"; "--nologo"; "--exec"; script ]

                    config.Environment["CLAIMCORE_TEST_CONNECTION"] <-
                        privateText "proxy-witness.connection" proxied

                    config.Environment["CLAIMCORE_TEST_INSTALLATION"] <- installation.ToString("D")

                    config.Environment["CLAIMCORE_TEST_GENERATION"] <-
                        previous.Generation.ToString("D")

                    config.Environment["CLAIMCORE_TEST_TRANSITION"] <- transition.ToString("D")
                    config)

            let! fenced =
                WitnessRestore.fence witnessOwner previous.SystemId installation transition token

            Expect.isSome fenced "A fresh connection finds the original fence after process kill"

            let! malformed = recordGap witnessDatabase installation transition "NOT_A_GAP_REASON"

            Expect.notEqual
                malformed.ExitCode
                (Nullable 0L)
                "The owner command refuses an unknown gap reason"

            Expect.stringContains
                malformed.Stderr
                "violates check constraint"
                "The bounded reason check rejects the decision"

            let mutable beforeGapRefused = false

            try
                let! _ =
                    RestoreAdmission.admitWitnessed
                        early
                        database.MasterKey
                        trusted
                        handle
                        witnessOwner
                        transition
                        None
                        token

                ()
            with :? OperatorFaultException as error when
                error.Fault = OperatorFault.StaleRecoveryPreparation ->
                beforeGapRefused <- true

            Expect.isTrue
                beforeGapRefused
                "An earlier target cannot reopen before the explicit gap record"

            let! gap = recordGap witnessDatabase installation transition "WAL_UNAVAILABLE"

            Expect.equal
                gap.ExitCode
                (Nullable 0L)
                "The documented witness-owner command records a typed gap"

            Expect.equal (gap.Stdout.Trim()) "t" "The fence acknowledges the decision"

            let! replacement =
                recordGap witnessDatabase installation transition "DISCONTINUOUS_HISTORY"

            Expect.equal
                (replacement.Stdout.Trim())
                "f"
                "A different reason cannot replace this transition's decision"

            let details: MaintenanceHandle = handle

            let generation =
                details.Generation.ToString(Globalization.CultureInfo.InvariantCulture)

            let pauseId = details.PauseId.ToString("D")

            let arguments =
                [
                    "admit-restored-installation"
                    "--checkpoint"
                    checkpoint
                    "--pause-generation"
                    generation
                    "--pause-id"
                    pauseId
                ]

            let start register witness arguments =
                let config = AdministrationProcess.start (binary :: arguments)
                config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- register
                config.Environment["CLAIMCORE_DATA_KEY_FILE"] <- database.DataKeyFile
                config.Environment["CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE"] <- witness
                config.Environment["CLAIMCORE_RESTORE_TRANSITION"] <- transition.ToString("D")
                config

            let proxyRegister label arguments =
                terminateAtCommit label (main 5433) 1 (fun proxied ->
                    start (privateText "proxy-register.connection" proxied) witnessFile arguments)

            let proxyWitness label commit arguments =
                terminateAtCommit label witnessConnection commit (fun proxied ->
                    start registerFile (privateText "proxy-witness.connection" proxied) arguments)

            do! proxyRegister "restore target identity reservation" arguments
            do! proxyWitness "witness decision seal" 1 arguments

            let! sealedFence =
                WitnessRestore.fence witnessOwner previous.SystemId installation transition token

            Expect.isSome
                sealedFence.Value.SealedEvidence
                "The decision survives an owner process kill before local preparation"

            let! repeatGap = recordGap witnessDatabase installation transition "WAL_UNAVAILABLE"

            Expect.equal
                (repeatGap.Stdout.Trim())
                "t"
                "An exact observation of the recorded reason still works after sealing"

            do! proxyRegister "local preparation" arguments

            let! prepared =
                DatabaseSession.read early token (fun connection ->
                    WitnessPrimary.read connection token)

            Expect.equal prepared.Generation transition "A fresh read finds the prepared transition"
            let! active = witness.IsActive(prepared, token)
            Expect.isFalse active "Killed local preparation alone cannot admit the target"

            do! proxyWitness "witness binding" 1 arguments

            let! bound =
                WitnessRestore.fence witnessOwner prepared.SystemId installation transition token

            Expect.isSome
                bound.Value.Preparation
                "A fresh connection finds the exact witness binding"

            let! active = witness.IsActive(prepared, token)
            Expect.isFalse active "Killed binding alone cannot admit the target"

            do! proxyWitness "witness activation" 2 arguments
            let! active = witness.IsActive(prepared, token)

            Expect.isTrue
                active
                "A fresh connection observes the committed activation after process kill"

            let! exit, body, _ =
                AdministrationProcess.execute (start registerFile witnessFile arguments)

            Expect.equal
                exit
                0
                "The original approval retries successfully from a fresh operator process"

            use result = Text.Json.JsonDocument.Parse body

            Expect.equal
                (result.RootElement.GetProperty("kind").GetString())
                "RESTORED_INSTALLATION_ADMITTED"
                "The exact original restore is returned"

            let reopening = [ "maintenance"; "resume"; generation; pauseId ]
            do! proxyWitness "reopen suppression" 1 reopening

            let! paused =
                DatabaseSession.read early token (fun connection ->
                    RegisterMaintenanceStore.read connection token)

            Expect.isTrue
                paused.Active
                "Process death after witness suppression leaves maintenance active"

            do! proxyRegister "local reopen" reopening

            let! resumed =
                DatabaseSession.read early token (fun connection ->
                    RegisterMaintenanceStore.read connection token)

            Expect.isFalse
                resumed.Active
                "The committed local reopen is observed from a fresh connection"

            use earlyRuntime =
                NpgsqlDataSource.Create(
                    endpoint database 5433 (PostgresFixture.runtimeConnectionString database)
                )

            use! cipher =
                DatabaseSession.read early token (fun connection ->
                    Baseline.loadCipher connection installation database.MasterKey token)

            let stage =
                LedgerStore(
                    earlyRuntime,
                    database.Installation,
                    cipher,
                    Transactions.timeouts,
                    witness = (witness, prepared)
                )
                :> IStore

            let earlyFixture = { database.Fixture with Store = stage }

            let! content =
                Attachments.download
                    (core earlyFixture earlyFixture.Owner)
                    (Attachments.attachmentGuid earlyFixture 21)

            match content with
            | Outcome.Succeeded _ -> ()
            | other ->
                failtestf
                    "A protected read after explicit gap admission must obtain a new witness acknowledgement: %A"
                    other

            let secondTransition = Guid.NewGuid()

            let! fenced =
                ownerSql
                    witnessDatabase
                    $"SELECT claimcore_witness.fence('{installation:D}','{transition:D}','{secondTransition:D}');"

            Expect.equal
                (fenced.Stdout.Trim())
                "t"
                "The next restore fences the previously admitted target"

            let! secondGap =
                recordGap witnessDatabase installation secondTransition "DISCONTINUOUS_HISTORY"

            Expect.equal
                (secondGap.Stdout.Trim())
                "t"
                "The second typed reason records an independent later decision"

            let replacementPause = Guid.NewGuid()

            let! pause =
                RegisterMaintenanceStore.change
                    late
                    database.MasterKey
                    (MaintenanceChange.Acquire(2L, replacementPause))
                    token

            let secondHandle = pause.State.Handle.Value

            let admitSecond approved independent =
                RestoreAdmission.admitWitnessed
                    late
                    database.MasterKey
                    approved
                    secondHandle
                    independent
                    secondTransition
                    None
                    token

            let rejectStaging work message =
                task {
                    try
                        let! _ = work
                        failtest message
                    with :? OperatorFaultException as error when
                        error.Fault = OperatorFault.StaleRecoveryPreparation ->
                        ()
                }

            do!
                rejectStaging
                    (admitSecond trusted witnessOwner)
                    "A valid gap cannot waive the older cutoff"

            let! unsealed =
                WitnessRestore.fence
                    witnessOwner
                    prepared.SystemId
                    installation
                    secondTransition
                    token

            Expect.equal
                unsealed.Value.RequiredCheckpoint.ExpiredBefore
                (Some approvedCutoff)
                "The new fence retains the active cutoff"

            Expect.isNone
                unsealed.Value.SealedEvidence
                "The weaker target is refused before sealing"

            do!
                loseCommit
                    "second gap restore activation"
                    witnessConnection
                    3
                    (admitSecond strongerCheckpoint)

            let! exact = admitSecond strongerCheckpoint witnessOwner
            let subsequent = exact |> Result.defaultWith (sprintf "%A" >> failtest)

            Expect.equal
                subsequent.Checkpoint.ExpiredBefore
                (Some approvedCutoff)
                "An equal cutoff can activate and retry exactly"

            Expect.isGreaterThan
                subsequent.Checkpoint.Position.Sequence
                strongerCheckpoint.Position.Sequence
                "The witness now admits a later verified ledger head"

            let! retained =
                ownerSql
                    witnessDatabase
                    $"SELECT reason FROM claimcore_witness.gaps WHERE installation='{installation:D}' ORDER BY recorded_at;"

            Expect.equal
                (retained.Stdout.Trim().Split('\n') |> Array.toList)
                [ "WAL_UNAVAILABLE"; "DISCONTINUOUS_HISTORY" ]
                "Both explicit gap decisions remain visible after another restore"

            let! activeTarget =
                DatabaseSession.read late token (fun connection ->
                    WitnessPrimary.read connection token)

            let! reopened =
                RestoreAdmission.resume late database.MasterKey witnessOwner secondHandle token

            use lateRuntime =
                NpgsqlDataSource.Create(
                    endpoint database 5434 (PostgresFixture.runtimeConnectionString database)
                )

            use! lateCipher =
                DatabaseSession.read late token (fun connection ->
                    Baseline.loadCipher connection installation database.MasterKey token)

            let restored =
                LedgerStore(
                    lateRuntime,
                    database.Installation,
                    lateCipher,
                    Transactions.timeouts,
                    witness = (witness, activeTarget)
                )
                :> IStore

            let restoredFixture =
                { database.Fixture with
                    Store = restored
                }

            let issued = trusted.ExpiredBefore.Value.AddMilliseconds 1.
            let lookup = { OperationId = OperationIds.at issued }

            let! observed =
                Service.operationGet (core restoredFixture restoredFixture.Owner) lookup token

            Expect.isTrue
                (DateTimeOffset.UtcNow < issued.AddMinutes 10.)
                "Time alone cannot settle this operation"

            Expect.equal
                observed.Outcome
                (Outcome.Succeeded OperationLookupResult.NotAccepted)
                "The active successor preserves expired-operation finality"

            let fenceWith generation proposed =
                task {
                    let! reply =
                        ownerSql
                            witnessDatabase
                            $"SELECT claimcore_witness.fence('{installation:D}','{generation:D}','{proposed:D}');"

                    Expect.equal
                        reply.ExitCode
                        (Nullable 0L)
                        "The owner fence command returns a decision"

                    return reply.Stdout.Trim()
                }

            let! retired = fenceWith secondTransition transition

            Expect.equal
                retired
                "f"
                "An earlier terminal transition cannot fence the active installation"

            let! originallyActive = witness.IsActive(activeTarget, token)

            Expect.isTrue
                originallyActive
                "The rejected retired ID leaves the active generation available"

            let fresh = Guid.NewGuid()
            let! freshAccepted = fenceWith secondTransition fresh

            Expect.equal
                freshAccepted
                "t"
                "A fresh transition still fences the current installation"

            let! samePending = fenceWith secondTransition fresh
            Expect.equal samePending "t" "An exact pending fence retry remains idempotent"
            let! staleGeneration = fenceWith transition fresh
            Expect.equal staleGeneration "f" "A stale generation cannot claim the live fence"
            let! conflicting = fenceWith secondTransition (Guid.NewGuid())
            Expect.equal conflicting "f" "A different proposal cannot replace the pending fence"

            let! thirdGap = recordGap witnessDatabase installation fresh "DISCONTINUOUS_HISTORY"
            Expect.equal (thirdGap.Stdout.Trim()) "t" "The next fence has a valid disclosure gap"

            let! oldPause =
                RegisterMaintenanceStore.change
                    early
                    database.MasterKey
                    (MaintenanceChange.Acquire(2L, Guid.NewGuid()))
                    token

            let newerCutoff = approvedCutoff.AddMinutes 1.

            let admitFresh target pause approved =
                RestoreAdmission.admitWitnessed
                    target
                    database.MasterKey
                    approved
                    pause
                    witnessOwner
                    fresh
                    None
                    token

            do!
                rejectStaging
                    (admitFresh
                        early
                        oldPause.State.Handle.Value
                        (withCutoff trusted.Position newerCutoff))
                    "A valid gap and newer cutoff cannot waive a missing previously admitted ledger prefix"

            let! nextPause =
                RegisterMaintenanceStore.change
                    late
                    database.MasterKey
                    (MaintenanceChange.Acquire(reopened.State.Generation, Guid.NewGuid()))
                    token

            let newer = withCutoff subsequent.Checkpoint.Position newerCutoff
            let! next = admitFresh late nextPause.State.Handle.Value newer
            let next = next |> Result.defaultWith (sprintf "%A" >> failtest)

            Expect.equal
                next.Checkpoint
                newer
                "A new ordinary restore can advance the cutoff with the retained prefix"

            let! audit =
                ownerSql
                    witnessDatabase
                    $"SELECT expiry_floor_ticks FROM claimcore_witness.preparations WHERE installation='{installation:D}' AND transition IN ('{transition:D}','{secondTransition:D}','{fresh:D}') ORDER BY prepared_at;"

            Expect.equal
                (audit.Stdout.Trim().Split('\n') |> Array.map Int64.Parse |> Array.toList)
                [ approvedCutoff.UtcTicks; approvedCutoff.UtcTicks; newerCutoff.UtcTicks ]
                "Earlier admitted cutoffs remain independently auditable"

            passed <- true
        finally
            if passed then
                IO.Directory.Delete(directory, true)
            else
                Console.Error.WriteLine("Synthetic owner-process fixture retained: " + directory)
    }

let private qualifyWitnessedRestore
    (database: PostgresFixture.Database)
    (witnessDatabase: Testcontainers.PostgreSql.PostgreSqlContainer)
    =
    task {
        let! runtimeConnection, witnessConnection =
            PostgresFixture.setupWitness witnessDatabase token

        use witnessOwner = NpgsqlDataSource.Create witnessConnection
        let installation = database.Installation
        let! initial = bootstrap database.Fixture

        let! enrolled =
            WitnessEnrollment.enroll
                database.OwnerSource
                witnessOwner
                installation.InstallationId
                token

        use witness =
            new RecoveryWitness(
                NpgsqlDataSource.Create runtimeConnection,
                installation.InstallationId
            )

        let storage =
            LedgerStore(
                database.RuntimeSource,
                installation,
                database.Cipher,
                Transactions.timeouts,
                caseReferenceEntropy = (fun () -> Array.zeroCreate CaseReference.EntropyBytes),
                witness = (witness, enrolled)
            )
            :> IStore

        let actor = { initial with Store = storage }
        do! grantCaseWork database.Fixture actor
        do! addPopulatedCase database actor
        do! Attachments.grantAttachmentRoles database.Fixture actor

        let! _ =
            Attachments.upload
                actor
                (Attachments.add database.Fixture 21 "witness.txt")
                Attachments.bytes

        let fixture = database.Fixture

        let values =
            Submissions.profile DomainInputs.ownerProfile
            |> Map.add
                (Submissions.input InputTarget.ProfileDigestEmail)
                (SubmissionValue.Text "recipient@allowed.example.test")

        let! profile =
            personSubmission
                fixture.Store
                (id fixture 810)
                fixture.Owner
                None
                "people.setProfile"
                values

        let! profiled = Service.executeSubmission actor profile token
        acceptedPeople profiled

        let target = CaseSelector.Reference populatedReference

        let assigned =
            Submissions.createRaw
                "work.update"
                (id fixture 811)
                (OperationBinding.Work(target, 0L))
                (Submissions.work
                    {
                        Responsible = WorkValue.Set((id fixture 1).ToString("D"))
                        Summary = WorkValue.Keep
                    })

        let! work = Service.executeSubmission actor assigned token
        accepted work |> ignore

        let dispatchDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays 2.)

        let reminderValues =
            Submissions.reminderPlan
                "Synthetic recovery reminder"
                (dispatchDate.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture))
            |> Map.add
                (Submissions.input InputTarget.ReminderPlanKind)
                (SubmissionValue.Text "DEADLINE")

        let reminder =
            Submissions.createRaw
                "reminder.create"
                (id fixture 812)
                (OperationBinding.Reminder(target, None))
                reminderValues

        let! reminded = Service.executeSubmission actor reminder token
        accepted reminded |> ignore

        use mailpit =
            new MailpitFixture.Mailpit(
                ClaimCore.Server.DigestSmtpTls.StartTls,
                Some "@allowed[.]example[.]test$"
            )

        do! mailpit.Start token
        use relay = new Net.Http.HttpClient(BaseAddress = mailpit.Api)

        let sent () =
            task {
                let! info = relay.GetStringAsync("info", token)
                use parsed = Text.Json.JsonDocument.Parse info

                return
                    parsed.RootElement
                        .GetProperty("RuntimeStats")
                        .GetProperty("SMTPAccepted")
                        .GetInt64()
            }

        let runDigest host date =
            let configuration: ClaimCore.Server.DigestConfiguration =
                {
                    Smtp = mailpit.Smtp
                    Time = TimeOnly(7, 0)
                    Language = "en"
                }

            ClaimCore.Server.DigestService.runDue
                IO.TextWriter.Null
                (Uri("https://claims.example.test"))
                host
                configuration
                (DateTimeOffset(date, TimeOnly(12, 0), TimeSpan.Zero))
                token

        let! weakerCheckpoint = checkpoint database
        let approvedCutoff = DateTimeOffset.UtcNow

        let unsettled =
            {
                OperationId = OperationIds.at (approvedCutoff.AddMinutes(-1.))
            }

        let trusted =
            InstallationCheckpoint.create
                weakerCheckpoint.InstallationId
                weakerCheckpoint.Position
                (Some approvedCutoff)
            |> Option.get

        let alias =
            NpgsqlConnectionStringBuilder(
                database.Container.GetConnectionString(),
                Host = "localhost"
            )

        use sameCluster = NpgsqlDataSource.Create alias.ConnectionString

        let! distinct =
            DatabaseSession.read sameCluster token (fun connection ->
                WitnessCluster.isIndependent enrolled.SystemId connection token)

        Expect.isFalse
            distinct
            "A different endpoint spelling still identifies the same provider cluster"

        use! blocking = witnessOwner.OpenConnectionAsync token
        use! lockTransaction = blocking.BeginTransactionAsync token

        let! _ =
            DatabaseSession.execute
                blocking
                "LOCK TABLE claimcore_witness.installations IN ACCESS EXCLUSIVE MODE"
                [||]
                token

        let settings = PostgresFixture.runtimeConnectionString database

        let! opened =
            ClaimCore.Server.Composition.Runtime.openWitnessedConnection
                settings
                runtimeConnection
                database.MasterKey
                token

        use host = opened |> Result.defaultWith (string >> failtest)
        let elapsed = Diagnostics.Stopwatch.StartNew()

        let! waiting =
            host.WithActor(
                database.Fixture.Owner,
                token,
                fun services ->
                    Service.operationGet
                        services
                        {
                            OperationId = OperationIds.at (DateTimeOffset.UtcNow.AddMinutes(-11.))
                        }
                        token
            )

        Expect.isNone waiting "A stalled witness cannot produce a conclusive absence"

        Expect.isLessThan
            elapsed.Elapsed
            (TimeSpan.FromSeconds 3.)
            "Witness phases have a short deadline"

        let! ready = host.Ready token
        Expect.isTrue ready "Local readiness remains independent of witness table locks"
        do! lockTransaction.RollbackAsync token

        let! beforeExpiry =
            host.WithActor(
                database.Fixture.Owner,
                token,
                fun services -> Service.operationGet services unsettled token
            )

        match beforeExpiry |> Option.map _.Outcome with
        | Some(Outcome.Succeeded(OperationLookupResult.Pending _)) -> ()
        | other -> failtestf "The operation starts pending on the original branch: %A" other

        do! takeBase database
        do! archivePoint database "before_witnessed_disclosure"

        let store =
            LedgerStore(
                database.RuntimeSource,
                installation,
                database.Cipher,
                Transactions.timeouts,
                witness = (witness, enrolled)
            )
            :> IStore

        let fixture = { database.Fixture with Store = store }

        let! active = witness.IsActive(enrolled, token)
        Expect.isTrue active "The fixture enrolled its durable independent witness"

        let! disclosed =
            Attachments.download
                (core fixture fixture.Owner)
                (Attachments.attachmentGuid fixture 21)

        match disclosed with
        | Outcome.Succeeded _ -> ()
        | other ->
            failtestf
                "Protected content must wait for the real independent acknowledgement: %A"
                other

        let! afterDisclosure = checkpoint database

        Expect.equal
            afterDisclosure.Position
            trusted.Position
            "Disclosure did not extend the business ledger"

        do! runDigest host dispatchDate
        let! originalSends = sent ()

        Expect.equal
            originalSends
            1L
            "The admitted original primary actually passed one digest to the isolated relay"

        // Digest dispatch now commits its own protected observation before sending.
        do! archivePoint database "after_witnessed_disclosure"

        let transition = Guid.NewGuid()

        let fence source =
            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection tx ->
                    task {
                        let! rows =
                            DatabaseSession.rows
                                connection
                                "SELECT claimcore_witness.fence($1,$2,$3)"
                                [|
                                    box installation.InstallationId
                                    box enrolled.Generation
                                    box transition
                                |]
                                token

                        Expect.isTrue
                            (DatabaseSession.value<bool> 0 rows.Head)
                            "The witness accepts the original fence"

                        do! DatabaseSession.commit tx token
                    })

        do! loseCommit "external fence" witnessConnection 1 fence
        do! fence witnessOwner
        let! stale = witness.Acknowledge(enrolled, token)
        Expect.isFalse stale "The old primary cannot acknowledge a disclosure after the fence"

        do! promote database "before_witnessed_disclosure" 5433
        do! promote database "after_witnessed_disclosure" 5434

        let pause = Guid.NewGuid()
        let handle = MaintenanceHandle.create 1L pause |> Option.get

        let prepare port =
            task {
                let source =
                    NpgsqlDataSource.Create(
                        endpoint database port (PostgresFixture.ownerConnectionString database)
                    )

                let! _ =
                    RegisterMaintenanceStore.change
                        source
                        database.MasterKey
                        (MaintenanceChange.Acquire(0L, pause))
                        token

                let! primary =
                    DatabaseSession.read source token (fun connection ->
                        WitnessPrimary.read connection token)

                let! history = history database port primary
                return source, history
            }

        let! early, earlyHistory = prepare 5433
        use early = early
        let mutable refused = false

        try
            let! _ =
                RestoreAdmission.admitWitnessed
                    early
                    database.MasterKey
                    trusted
                    handle
                    witnessOwner
                    transition
                    earlyHistory
                    token

            ()
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            refused <- true

        Expect.isTrue
            refused
            "The same ledger on an earlier fork cannot prove acknowledged disclosure coverage"

        let! late, lateHistory = prepare 5434
        use late = late

        let replaceLostTarget port bindExternally expected =
            task {
                do! promote database "after_witnessed_disclosure" port
                let! candidate, candidateHistory = prepare port
                use candidate = candidate

                let admit primary independent =
                    RestoreAdmission.admitWitnessed
                        primary
                        database.MasterKey
                        trusted
                        handle
                        independent
                        expected
                        candidateHistory
                        token

                let targetConnection =
                    endpoint database port (PostgresFixture.ownerConnectionString database)

                do!
                    loseCommit
                        "lost target's identity reservation"
                        targetConnection
                        1
                        (fun source -> admit source witnessOwner)

                do!
                    loseCommit "lost target's local preparation" targetConnection 1 (fun source ->
                        admit source witnessOwner)

                if bindExternally then
                    do!
                        loseCommit
                            "lost target's witness binding"
                            witnessConnection
                            1
                            (admit candidate)

                let! prepared =
                    DatabaseSession.read candidate token (fun connection ->
                        WitnessPrimary.read connection token)

                Expect.equal
                    prepared.Generation
                    expected
                    "The lost target really committed local preparation"

                let! before =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        expected
                        token

                Expect.equal
                    before.Value.Preparation.IsSome
                    bindExternally
                    "The lost target is at the intended boundary"

                let! candidateActive = witness.IsActive(prepared, token)
                Expect.isFalse candidateActive "A prepared candidate remains unavailable"

                try
                    let! _ =
                        RestoreAdmission.admitWitnessed
                            late
                            database.MasterKey
                            trusted
                            handle
                            witnessOwner
                            expected
                            lateHistory
                            token

                    failtest
                        "An ordinary retry must refuse another target even when its WAL covers the original fork"
                with :? OperatorFaultException as error when
                    error.Fault = OperatorFault.StaleRecoveryPreparation ->
                    ()

                do!
                    runPostgres
                        database
                        [
                            "pg_ctl"
                            "-w"
                            "-D"
                            $"{storageDirectory}/attachment-restore-{port}"
                            "stop"
                            "-m"
                            "fast"
                        ]

                let replacement = Guid.NewGuid()

                do!
                    loseCommit "owner supersession" witnessConnection 1 (fun source ->
                        DatabaseSession.transaction
                            source
                            Data.IsolationLevel.ReadCommitted
                            token
                            (fun connection transaction ->
                                task {
                                    let! reply =
                                        DatabaseSession.rows
                                            connection
                                            "SELECT claimcore_witness.supersede_lost_target($1,$2,$3)"
                                            [|
                                                box trusted.InstallationId
                                                box expected
                                                box replacement
                                            |]
                                            token

                                    Expect.isTrue
                                        (DatabaseSession.value<bool> 0 reply.Head)
                                        "The owner supersedes the lost target"

                                    do! DatabaseSession.commit transaction token
                                }))

                let! result =
                    supersedeTarget witnessDatabase trusted.InstallationId expected replacement

                Expect.equal result.ExitCode (Nullable 0L) "The documented owner command ran"

                Expect.equal
                    (result.Stdout.Trim())
                    "t"
                    "The owner can supersede an unreachable prepared target"

                let! old =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        expected
                        token

                Expect.isNone old "A stale admission cannot use the superseded transition"

                let! successor =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        replacement
                        token

                Expect.equal
                    successor
                    (before
                     |> Option.map (fun prior ->
                         { prior with
                             SealedEvidence = None
                             SealedApproval = None
                             Preparation = None
                         }))
                    "Only the attempt changed. The original WAL requirement and dispatch floors remain."

                let! audit =
                    ownerSql
                        witnessDatabase
                        $"""SELECT count(*) FROM claimcore_witness.transitions successor
                            JOIN claimcore_witness.transitions lost
                              ON lost.installation=successor.installation AND lost.transition=successor.supersedes
                            WHERE successor.installation='{trusted.InstallationId:D}'
                              AND successor.transition='{replacement:D}' AND lost.transition='{expected:D}'
                              AND successor.superseded_at IS NOT NULL
                              AND lost.sealed_target='{prepared.PrimaryId:D}' AND octet_length(lost.sealed_approval)=32;"""

                Expect.equal
                    (audit.Stdout.Trim())
                    "1"
                    "The witness permanently records which attempt was superseded"

                let! blocked =
                    host.WithActor(
                        fixture.Owner,
                        token,
                        fun _ -> failtest "Fenced actor work must never enter the callback"
                    )

                Expect.isNone blocked "The original host admits no actor during supersession"
                do! runDigest host (dispatchDate.AddDays 1)
                let! fencedSends = sent ()
                Expect.equal fencedSends 1L "The fenced host makes no additional relay attempt"
                return replacement
            }

        let! secondAttempt = replaceLostTarget 5435 false transition
        let! transition = replaceLostTarget 5436 true secondAttempt

        let! retainedBinding =
            ownerSql
                witnessDatabase
                $"SELECT count(*) FROM claimcore_witness.preparations WHERE installation='{trusted.InstallationId:D}' AND transition='{secondAttempt:D}';"

        Expect.equal
            (retainedBinding.Stdout.Trim())
            "1"
            "Supersession preserves the lost target's binding"

        try
            let! _ =
                RestoreAdmission.admitWitnessed
                    early
                    database.MasterKey
                    trusted
                    handle
                    witnessOwner
                    transition
                    earlyHistory
                    token

            failtest "The successor must still cover the original disclosure"
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            ()

        let mutable wrongHistoryRefused = false

        try
            let! _ =
                RestoreAdmission.admitWitnessed
                    late
                    database.MasterKey
                    trusted
                    handle
                    witnessOwner
                    transition
                    earlyHistory
                    token

            ()
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            wrongHistoryRefused <- true

        Expect.isTrue
            wrongHistoryRefused
            "A sibling target's history is refused even when its filename and timeline number match"

        Expect.isNone
            weakerCheckpoint.ExpiredBefore
            "The older checkpoint lacks the approved expiry cutoff"

        try
            let! _ =
                RestoreAdmission.admitWitnessed
                    late
                    database.MasterKey
                    weakerCheckpoint
                    handle
                    witnessOwner
                    transition
                    lateHistory
                    token

            failtest "The adequate successor cannot restore a weaker checkpoint"
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            ()

        let! unsealed =
            WitnessRestore.fence
                witnessOwner
                enrolled.SystemId
                trusted.InstallationId
                transition
                token

        Expect.isNone
            unsealed.Value.SealedEvidence
            "Refusing the weaker checkpoint leaves the successor unsealed"

        let admit primary independent =
            RestoreAdmission.admitWitnessed
                primary
                database.MasterKey
                trusted
                handle
                independent
                transition
                lateHistory
                token

        let primaryConnection =
            endpoint database 5434 (PostgresFixture.ownerConnectionString database)

        let refuseLateGap stage =
            task {
                let! before =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        transition
                        token

                let! rejected =
                    recordGap witnessDatabase trusted.InstallationId transition "WAL_UNAVAILABLE"

                Expect.equal rejected.ExitCode (Nullable 0L) "The owner gap command exits normally"

                Expect.equal
                    (rejected.Stdout.Trim())
                    "f"
                    ($"{stage}: a newly proposed gap is refused")

                let! after =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        transition
                        token

                Expect.equal
                    after
                    before
                    ($"{stage}: the original sealed decision and target binding remain unchanged")
            }

        do! loseCommit "witness decision seal" witnessConnection 1 (admit late)

        let! beforePreparation =
            DatabaseSession.read late token (fun connection -> WitnessPrimary.read connection token)

        Expect.notEqual
            beforePreparation.Generation
            transition
            "A lost seal acknowledgement occurs before local preparation"

        let changedCutoff =
            InstallationCheckpoint.create
                trusted.InstallationId
                trusted.Position
                (Some(DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            |> Option.get

        try
            let! _ =
                RestoreAdmission.admitWitnessed
                    late
                    database.MasterKey
                    changedCutoff
                    handle
                    witnessOwner
                    transition
                    lateHistory
                    token

            failtest "A changed checkpoint must be refused immediately after the seal"
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            ()

        do! refuseLateGap "After seal and before local preparation"

        do!
            loseCommit "local preparation" primaryConnection 1 (fun source ->
                admit source witnessOwner)

        let! localPrepared =
            DatabaseSession.read late token (fun connection -> WitnessPrimary.read connection token)

        Expect.equal
            localPrepared.Generation
            transition
            "Retry can read back the exact durable local preparation"

        let! beforeBinding = witness.IsActive(localPrepared, token)
        Expect.isFalse beforeBinding "Local preparation alone cannot admit the target"

        let differentHash = EntryHash.ofBytes (Array.create 32 0x6duy) |> Option.get

        let differentSequence =
            Sequence.create (Sequence.value trusted.Position.Sequence + 1L) |> Option.get

        let distinctApprovals =
            [
                "installation",
                InstallationCheckpoint.create
                    (Guid.NewGuid())
                    trusted.Position
                    trusted.ExpiredBefore
                "hash",
                InstallationCheckpoint.create
                    trusted.InstallationId
                    (LedgerPosition.create trusted.Position.Sequence differentHash)
                    trusted.ExpiredBefore
                "position",
                InstallationCheckpoint.create
                    trusted.InstallationId
                    (LedgerPosition.create differentSequence trusted.Position.EntryHash)
                    trusted.ExpiredBefore
                "advanced cutoff",
                InstallationCheckpoint.create
                    trusted.InstallationId
                    trusted.Position
                    (Some(DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            ]

        let rejectChanged stage =
            task {
                let! before =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        transition
                        token

                let! localBefore =
                    DatabaseSession.read late token (fun connection ->
                        WitnessPrimary.read connection token)

                for field, different in distinctApprovals do
                    let mutable refused = false

                    try
                        let! _ =
                            RestoreAdmission.admitWitnessed
                                late
                                database.MasterKey
                                different.Value
                                handle
                                witnessOwner
                                transition
                                lateHistory
                                token

                        ()
                    with :? OperatorFaultException as error when
                        error.Fault = OperatorFault.StaleRecoveryPreparation ->
                        refused <- true

                    Expect.isTrue refused ($"{stage}: a different {field} visibly refuses.")

                let mutable archiveRefused = false

                try
                    let! _ =
                        RestoreAdmission.admitWitnessed
                            late
                            database.MasterKey
                            trusted
                            handle
                            witnessOwner
                            transition
                            None
                            token

                    ()
                with :? OperatorFaultException as error when
                    error.Fault = OperatorFault.StaleRecoveryPreparation ->
                    archiveRefused <- true

                Expect.isTrue
                    archiveRefused
                    ($"{stage}: omitting the prepared archive evidence visibly refuses.")

                let! after =
                    WitnessRestore.fence
                        witnessOwner
                        enrolled.SystemId
                        trusted.InstallationId
                        transition
                        token

                let! localAfter =
                    DatabaseSession.read late token (fun connection ->
                        WitnessPrimary.read connection token)

                Expect.equal
                    after
                    before
                    ($"{stage}: refused retries cannot bind, activate or replace the witness preparation.")

                Expect.equal
                    localAfter.Generation
                    localBefore.Generation
                    ($"{stage}: the local generation is preserved.")

                Expect.equal
                    localAfter.PrimaryId
                    localBefore.PrimaryId
                    ($"{stage}: the local target is preserved.")
            }

        do! rejectChanged "After local preparation"
        do! refuseLateGap "After local preparation"
        do! loseCommit "external target binding" witnessConnection 1 (admit late)
        do! refuseLateGap "After witness binding"
        let! beforeActivation = witness.IsActive(localPrepared, token)
        Expect.isFalse beforeActivation "Binding a prepared target cannot enable service"

        do! loseCommit "external activation" witnessConnection 2 (admit late)
        let! afterActivation = witness.IsActive(localPrepared, token)

        Expect.isTrue
            afterActivation
            "Exact external activation is independently visible after acknowledgement loss"

        do! rejectChanged "After external activation"

        let! admitted =
            RestoreAdmission.admitWitnessed
                late
                database.MasterKey
                trusted
                handle
                witnessOwner
                transition
                lateHistory
                token

        let restored = admitted |> Result.defaultWith (sprintf "%A" >> failtest)

        Expect.equal
            restored.Checkpoint.ExpiredBefore
            (Some approvedCutoff)
            "The inherited cutoff survives activation"

        let! prepared =
            DatabaseSession.read late token (fun connection -> WitnessPrimary.read connection token)

        Expect.equal
            prepared.Generation
            transition
            "Local generation matches the external transition"

        let! current = witness.IsActive(prepared, token)
        Expect.isTrue current "External activation admits this local primary identity"

        let restoredEndpoint =
            endpoint database 5434 (PostgresFixture.runtimeConnectionString database)

        let! restoredHost =
            ClaimCore.Server.Composition.Runtime.openWitnessedConnection
                restoredEndpoint
                runtimeConnection
                database.MasterKey
                token

        use restoredHost = restoredHost |> Result.defaultWith (string >> failtest)
        do! runDigest restoredHost dispatchDate
        let! whilePaused = sent ()
        Expect.equal whilePaused 1L "A verification host in maintenance makes no relay attempt"

        let staleDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1.))
        let mutable rolledOver = false

        try
            let! _ =
                RegisterMaintenanceStore.changeForReopen
                    late
                    database.MasterKey
                    handle
                    (prepared.Generation, prepared.PrimaryId, staleDay)
                    token

            ()
        with :? OperatorFaultException as error when
            error.Fault = OperatorFault.StaleRecoveryPreparation ->
            rolledOver <- true

        Expect.isTrue
            rolledOver
            "The actual head rejects a suppression observation from an earlier business date"

        let resume primary independent =
            RestoreAdmission.resume primary database.MasterKey independent handle token

        do! loseCommit "reopen suppression floor" witnessConnection 1 (resume late)

        let! stillPaused =
            DatabaseSession.read late token (fun connection ->
                RegisterMaintenanceStore.read connection token)

        Expect.isTrue
            stillPaused.Active
            "Unknown witness suppression acknowledgement leaves local maintenance active"

        do!
            loseCommit "local maintenance release" primaryConnection 1 (fun source ->
                resume source witnessOwner)

        let! reopened =
            DatabaseSession.read late token (fun connection ->
                RegisterMaintenanceStore.read connection token)

        Expect.isFalse
            reopened.Active
            "The original release can be observed after its acknowledgement is lost"

        let! expired =
            restoredHost.WithActor(
                fixture.Owner,
                token,
                fun services -> Service.operationGet services unsettled token
            )

        Expect.isTrue
            (DateTimeOffset.UtcNow < approvedCutoff.AddMinutes 9.)
            "The lookup is still before ordinary time-based expiry"

        Expect.equal
            (expired |> Option.map _.Outcome)
            (Some(Outcome.Succeeded OperationLookupResult.NotAccepted))
            "The inherited frontier makes the formerly pending operation's absence final"

        do! runDigest restoredHost (dispatchDate.AddDays(-1))
        do! runDigest restoredHost dispatchDate
        let! suppressed = sent ()

        Expect.equal
            suppressed
            1L
            "The recovered host suppresses the old dispatch day and a backward clock"

        do! runDigest restoredHost (dispatchDate.AddDays 1)
        do! runDigest restoredHost (dispatchDate.AddDays 1)
        let! later = sent ()

        Expect.equal
            later
            2L
            "The next business date allows one actual SMTP dispatch despite repeat polling"

        let! events =
            DatabaseSession.read late token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM claimcore.disclosure_trail"
                    [||]
                    token)

        Expect.isGreaterThan
            (DatabaseSession.value<int64> 0 events.Head)
            0L
            "Acknowledged disclosure survived actual WAL replay"

        let extensionDraft =
            Submissions.createRaw
                "reminder.create"
                (OperationIds.fresh ())
                (OperationBinding.Reminder(target, None))
                (Submissions.reminderPlan
                    "Retained ledger prefix"
                    (dispatchDate
                        .AddDays(1)
                        .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture))
                 |> Map.add
                     (Submissions.input InputTarget.ReminderPlanKind)
                     (SubmissionValue.Text "DEADLINE"))

        let! extension =
            restoredHost.WithActor(
                fixture.Owner,
                token,
                fun services -> Service.executeSubmission services extensionDraft token
            )

        Expect.isSome
            extension
            "The admitted active target extends the ledger before the next restore"

        extension.Value |> accepted |> ignore

        do!
            probeGapAndProcessKills
                database
                witnessDatabase
                witness
                witnessOwner
                witnessConnection
                trusted
                prepared
                early
                late
                handle
    }

let private booleanQuery connection sql parameters =
    task {
        let! rows = DatabaseSession.rows connection sql parameters token
        return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
    }

let private expectLock observer (contender: NpgsqlConnection) =
    task {
        let deadline = Stopwatch.StartNew()
        let mutable waiting = false

        while not waiting && deadline.Elapsed < TimeSpan.FromSeconds 5. do
            let! observed =
                booleanQuery
                    observer
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid=$1 AND wait_event_type='Lock')"
                    [| box contender.ProcessID |]

            waiting <- observed

            if not waiting then
                do! Task.Delay(10, token)

        Expect.isTrue waiting "The second owner command really waits on PostgreSQL"
    }

let private decisionRace (source: NpgsqlDataSource) gapFirst =
    task {
        use! first = source.OpenConnectionAsync token
        use! second = source.OpenConnectionAsync token
        use! observer = source.OpenConnectionAsync token

        let installation, generation, target, transition =
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()

        let! _ =
            DatabaseSession.execute
                first
                """INSERT INTO claimcore_witness.installations(
                       installation,generation,primary_id,system_id,timeline,disclosure_lsn,admitted_sequence,admitted_hash)
                   VALUES($1,$2,$3,19,'00000001','0/100',1,decode(repeat('00',32),'hex'))"""
                [| box installation; box generation; box target |]
                token

        let call = booleanQuery

        let! fenced =
            call
                first
                "SELECT claimcore_witness.fence($1,$2,$3)"
                [| box installation; box generation; box transition |]

        Expect.isTrue fenced "The owner reserves a fresh transition"

        let digest connection =
            task {
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore_witness.fence_digest($1,$2)"
                        [| box installation; box transition |]
                        token

                return DatabaseSession.value<byte array> 0 (List.exactlyOne rows)
            }

        let! expected = digest first

        let proposedTarget = Guid.NewGuid()

        let seal connection evidence fork =
            call
                connection
                "SELECT claimcore_witness.seal_fence($1,$2,$3,19,'00000002','0/200',$4::pg_lsn,$5,$3,$3)"
                [|
                    box installation
                    box transition
                    box evidence
                    box fork
                    box proposedTarget
                |]

        let gap connection =
            call
                connection
                "SELECT claimcore_witness.record_gap($1,$2,'WAL_UNAVAILABLE')"
                [| box installation; box transition |]

        let! unsealedBinding =
            call
                first
                "SELECT claimcore_witness.prepare_target($1,$2,$3,19,'00000002',$4,$5,1,decode(repeat('00',32),'hex'),NULL,'2030-01-01'::timestamptz)"
                [|
                    box installation
                    box transition
                    box proposedTarget
                    box expected
                    box expected
                |]

        Expect.isFalse unsealedBinding "A draft digest cannot bind a target"
        let! insufficient = seal first expected "0/50"
        Expect.isFalse insufficient "An uncovered fork cannot close the owner's gap decision"

        use! transaction = first.BeginTransactionAsync token
        let! winner = if gapFirst then gap first else seal first expected "0/110"
        Expect.isTrue winner "Hold the first successful mutation uncommitted"

        let contender =
            if gapFirst then
                seal second expected "0/110"
            else
                gap second

        do! expectLock observer second
        do! transaction.CommitAsync token
        let! accepted = contender
        Expect.isFalse accepted "The waiting command cannot replace the committed decision"

        let! observed = digest observer

        if gapFirst then
            Expect.notEqual observed expected "The committed gap changed the draft digest"
            let! retried = seal observer observed "0/50"

            Expect.isTrue
                retried
                "A fresh prepare can seal the recorded gap after the stale attempt"
        else
            Expect.equal observed expected "The seal keeps the original decision usable"
            let! retried = seal observer observed "0/110"
            Expect.isTrue retried "The exact seal can be retried"
    }

let private supersessionRace (source: NpgsqlDataSource) replacementFirst =
    task {
        use! first = source.OpenConnectionAsync token
        use! second = source.OpenConnectionAsync token
        use! observer = source.OpenConnectionAsync token

        let installation, generation, primary =
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()

        let attempt, replacement, target = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        let preparation = Array.create 32 0x19uy

        let! _ =
            DatabaseSession.execute
                first
                """INSERT INTO claimcore_witness.installations(
                       installation,generation,primary_id,system_id,timeline,disclosure_lsn,latest_dispatch,suppress_through,
                       admitted_sequence,admitted_hash,expiry_floor_ticks)
                   VALUES($1,$2,$3,19,'00000001','0/100','2030-01-01','2030-01-02',2,decode(repeat('00',32),'hex'),1000)"""
                [| box installation; box generation; box primary |]
                token

        let call = booleanQuery

        let! runtimeCanSupersede =
            call
                first
                "SELECT has_function_privilege('claimcore_witness_runtime','claimcore_witness.supersede_lost_target(uuid,uuid,uuid)','EXECUTE')"
                [||]

        Expect.isFalse runtimeCanSupersede "The runtime role cannot supersede a restore"

        let! fenced =
            call
                first
                "SELECT claimcore_witness.fence($1,$2,$3)"
                [| box installation; box generation; box attempt |]

        Expect.isTrue fenced "The witness fences the original primary"

        let supersede connection expected successor =
            call
                connection
                "SELECT claimcore_witness.supersede_lost_target($1,$2,$3)"
                [| box installation; box expected; box successor |]

        let! unsealed = supersede first attempt replacement
        Expect.isFalse unsealed "There is no lost prepared target before sealing"

        let! gap =
            call
                first
                "SELECT claimcore_witness.record_gap($1,$2,'WAL_UNAVAILABLE')"
                [| box installation; box attempt |]

        Expect.isTrue gap "The fixture records an explicit gap on the original attempt"

        let seal connection transition primaryId checkpointDigest =
            call
                connection
                """SELECT claimcore_witness.seal_fence($1,$2,claimcore_witness.fence_digest($1,$2),
                           19,'00000002','0/200',NULL,$3,$4,$5)"""
                [|
                    box installation
                    box transition
                    box primaryId
                    box preparation
                    box checkpointDigest
                |]

        let bindCheckpoint connection transition primaryId sequence hash expiry =
            call
                connection
                """SELECT claimcore_witness.prepare_target($1,$2,$3,19,'00000002',
                           claimcore_witness.fence_digest($1,$2),$4,$5,$6,$7,'2030-01-01'::timestamptz)"""
                [|
                    box installation
                    box transition
                    box primaryId
                    box preparation
                    box sequence
                    box hash
                    DatabaseSession.parameter expiry
                |]

        let prefixHash = Array.zeroCreate<byte> 32

        let bind connection transition primaryId =
            bindCheckpoint connection transition primaryId 2L prefixHash (Some 1000L)

        let activate connection transition primaryId =
            call
                connection
                "SELECT claimcore_witness.activate($1,$2,19,'00000002','0/200',NULL,'2030-01-02',$3,$4)"
                [| box installation; box transition; box primaryId; box preparation |]

        let! sealedAttempt = seal first attempt target preparation
        Expect.isTrue sealedAttempt "The original gap allows a seal"
        let! oldExpiry = bindCheckpoint first attempt target 2L prefixHash (Some 999L)
        Expect.isFalse oldExpiry "A gap cannot bind a lower cutoff"
        let! missingExpiry = bindCheckpoint first attempt target 2L prefixHash None
        Expect.isFalse missingExpiry "A gap cannot bind an absent cutoff"
        let! shorterPrefix = bindCheckpoint first attempt target 1L prefixHash (Some 1000L)
        Expect.isFalse shorterPrefix "A gap cannot bind a shorter ledger"
        let! wrongHash = bindCheckpoint first attempt target 2L preparation (Some 1000L)
        Expect.isFalse wrongHash "A gap cannot bind an equal position with a different hash"
        let! bound = bind first attempt target
        Expect.isTrue bound "The old candidate is externally bound"
        let! retiredGeneration = supersede first attempt generation
        Expect.isFalse retiredGeneration "The pre-fence generation is forever reserved"

        use! transaction = first.BeginTransactionAsync token

        let! winner =
            if replacementFirst then
                supersede first attempt replacement
            else
                activate first attempt target

        Expect.isTrue winner "Hold the winning mutation uncommitted"

        let contender =
            if replacementFirst then
                activate second attempt target
            else
                supersede second attempt replacement

        do! expectLock observer second
        do! transaction.CommitAsync token
        let! accepted = contender
        Expect.isFalse accepted "The losing mutation is refused after the lock clears"

        if replacementFirst then
            let! stale = supersede first attempt (Guid.NewGuid())
            Expect.isFalse stale "The original attempt cannot supersede a different pending attempt"
            let! reused = supersede first replacement attempt
            Expect.isFalse reused "The superseded identity cannot be reused"
            let! originalBind = bind first attempt target
            Expect.isFalse originalBind "The superseded binding cannot be used"

            let! currentFence = WitnessRestore.fence source 19L installation replacement token

            Expect.isTrue
                currentFence.Value.Gap
                "The adapter reads the original gap on the successor"

            let! coherent =
                call
                    first
                    """SELECT count(*)=1 AND min(reason)='WAL_UNAVAILABLE'
                       FROM claimcore_witness.gaps WHERE installation=$1"""
                    [| box installation |]

            Expect.isTrue coherent "Supersession adds no new gap record"

            let! changed =
                call
                    first
                    "SELECT claimcore_witness.record_gap($1,$2,'DISCONTINUOUS_HISTORY')"
                    [| box installation; box replacement |]

            Expect.isFalse changed "The successor cannot revise the original decision"
            let successor = Guid.NewGuid()
            let! premature = bind first replacement successor
            Expect.isFalse premature "The successor must seal before binding"
            let! changedCheckpoint = seal first replacement successor (Array.create 32 0x73uy)

            Expect.isFalse
                changedCheckpoint
                "The witness refuses to seal a different checkpoint for the successor"

            let! successorSeal = seal first replacement successor preparation
            Expect.isTrue successorSeal "The original gap is sufficient for the successor to seal"
            let! retiredAttempt = supersede first replacement attempt
            Expect.isFalse retiredAttempt "A sealed pending replacement cannot reuse an old attempt"
            let! successorBind = bind first replacement successor
            Expect.isTrue successorBind "The successor can bind its own target"
            let! activated = activate first replacement successor
            Expect.isTrue activated "Only the successor may activate"
        else
            let! stillActive =
                call
                    first
                    "SELECT claimcore_witness.is_active($1,$2,19,'00000002',$3,'2030-01-01'::timestamptz)"
                    [| box installation; box attempt; box target |]

            Expect.isTrue stillActive "Refused supersession leaves the winning activation available"

            let! ordinaryFence =
                call
                    first
                    "SELECT claimcore_witness.fence($1,$2,$3)"
                    [| box installation; box attempt; box replacement |]

            Expect.isTrue
                ordinaryFence
                "The unused ID can begin an ordinary restore from the active generation"
    }

[<Tests>]
let witnessSupersessionRaces =
    testCaseAsync
        "witness owner supersession and activation serialize in both orders"
        (async {
            let witness = PostgresFixture.createWitness ()

            try
                let! _, owner = PostgresFixture.setupWitness witness token |> Async.AwaitTask
                use source = NpgsqlDataSource.Create owner
                do! supersessionRace source true |> Async.AwaitTask
                do! supersessionRace source false |> Async.AwaitTask
            finally
                witness.DisposeAsync().AsTask().GetAwaiter().GetResult()
        })

[<Tests>]
let witnessDecisionRaces =
    testCaseAsync
        "witness decision seal and owner gap serialize in both orders"
        (async {
            let witness = PostgresFixture.createWitness ()

            try
                let! _, owner = PostgresFixture.setupWitness witness token |> Async.AwaitTask
                use source = NpgsqlDataSource.Create owner
                do! decisionRace source true |> Async.AwaitTask
                do! decisionRace source false |> Async.AwaitTask
            finally
                witness.DisposeAsync().AsTask().GetAwaiter().GetResult()
        })

[<Tests>]
let witnessTests =
    testCaseAsync
        "independent witness fences an earlier physical branch and preserves exact restore approvals and gaps through process death"
        (async {
            let! database = PostgresFixture.createArchived () |> Async.AwaitTask
            let witness = PostgresFixture.createWitness ()

            try
                do! qualifyWitnessedRestore database witness |> Async.AwaitTask
            finally
                witness.DisposeAsync().AsTask().GetAwaiter().GetResult()
                database.Fixture.Dispose()
        })
