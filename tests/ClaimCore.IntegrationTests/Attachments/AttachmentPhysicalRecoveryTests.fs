module ClaimCore.IntegrationTests.AttachmentPhysicalRecoveryTests

open System
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
