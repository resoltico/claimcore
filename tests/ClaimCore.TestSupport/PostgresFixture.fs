module ClaimCore.TestSupport.PostgresFixture

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open Testcontainers.PostgreSql
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures


type private SetupStage =
    | ContainerReadiness
    | DatabaseRoles
    | SchemaInitialization
    | RuntimeAdmission

let private failureCategory (error: exn) =
    match error with
    | :? PostgresException -> "POSTGRES"
    | :? NpgsqlException as failure ->
        match failure.InnerException with
        | :? TimeoutException -> "NPGSQL_TIMEOUT"
        | :? Net.Sockets.SocketException -> "NPGSQL_SOCKET"
        | _ -> "NPGSQL"
    | :? TimeoutException -> "TIMEOUT"
    | :? OperationCanceledException -> "CANCELLATION"
    | :? Net.Sockets.SocketException -> "SOCKET"
    | :? IO.IOException -> "IO"
    | :? ArgumentException -> "ARGUMENT"
    | _ -> "OTHER"

let private owner =
    PrincipalKey.human "https://issuer.example/" "synthetic-owner"
    |> Result.defaultWith (string >> invalidOp)

[<NoEquality; NoComparison>]
type internal Database =
    {
        Container: PostgreSqlContainer
        OwnerSource: NpgsqlDataSource
        SuperuserSource: NpgsqlDataSource
        RuntimeSource: NpgsqlDataSource
        MasterKey: MasterKey
        Cipher: InstallationCipher
        DataKeyFile: string
        Fixture: Fixture
        Installation: Installation
    }

let private withKeyBytes action =
    let bytes = RandomNumberGenerator.GetBytes MasterKey.Size

    try
        action bytes
    finally
        CryptographicOperations.ZeroMemory bytes

let internal newMasterKey () =
    withKeyBytes (MasterKey.admit >> Option.get)

let internal writeDataKeyFile path =
    withKeyBytes (fun bytes ->
        use file =
            new FileStream(
                path,
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                )
            )

        file.Write bytes
        MasterKey.admit bytes |> Option.get)

let internal writeKeyFile path =
    use _key = writeDataKeyFile path
    ()

let private ownerRole = "claimcore_owner"
let private ownerPassword = "synthetic-owner-only"
let private runtimeRole = "claimcore_runtime"
let private runtimePassword = "synthetic-only"

let private ownerSettings (container: PostgreSqlContainer) =
    NpgsqlConnectionStringBuilder(
        container.GetConnectionString(),
        Username = ownerRole,
        Password = ownerPassword
    )

let private runtimeSettings (container: PostgreSqlContainer) =
    NpgsqlConnectionStringBuilder(
        container.GetConnectionString(),
        Username = runtimeRole,
        Password = runtimePassword
    )

let internal ownerConnection (container: PostgreSqlContainer) =
    (ownerSettings container).ConnectionString

let internal ownerConnectionString (database: Database) = ownerConnection database.Container

let internal runtimeConnection (container: PostgreSqlContainer) =
    (runtimeSettings container).ConnectionString

let internal runtimeConnectionString (database: Database) = runtimeConnection database.Container

let internal createRoles (superuser: NpgsqlDataSource) token =
    task {
        use! connection = superuser.OpenConnectionAsync(token)
        use identifiers = new NpgsqlCommandBuilder()
        let database = identifiers.QuoteIdentifier(connection.Database)

        let! _ =
            DatabaseSession.execute
                connection
                $"""
                CREATE ROLE {ownerRole} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{ownerPassword}';
                CREATE ROLE {runtimeRole} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{runtimePassword}';
                ALTER DATABASE {database} OWNER TO {ownerRole};
                REVOKE ALL ON DATABASE {database} FROM PUBLIC;
                GRANT CONNECT ON DATABASE {database} TO {runtimeRole};
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                """
                [||]
                token

        return ()
    }

let private dataRoot = "/var/lib/postgresql"
let private seedMount = "/claimcore-postgres-seed"

let private builder () =
    let image, initdbArguments = PostgresBaseline.configuration ()

    (new PostgreSqlBuilder(image))
        .WithEnvironment("POSTGRES_INITDB_ARGS", initdbArguments)
        .WithLabel("claimcore.test", "ledger-store")
        .WithLabel("claimcore.test-run", Guid.NewGuid().ToString("D"))
        .WithWaitStrategy(ClaimCore.TestSupport.PostgresReadiness.strategy ())

let private fixtureContainer (configure: PostgreSqlBuilder -> PostgreSqlBuilder) =
    builder().WithTmpfsMount(dataRoot) |> configure |> _.Build()

let internal empty () = fixtureContainer (fun value -> value)

let private createSeed () =
    task {
        let volume =
            VolumeBuilder()
                .WithName("claimcore-postgres-seed-" + Guid.NewGuid().ToString("N"))
                .WithCleanUp(true)
                .Build()

        try
            do! volume.CreateAsync()
            use seed = builder().WithVolumeMount(volume.Name, dataRoot).Build()
            do! seed.StartAsync()
            do! seed.StopAsync()
            let! exitCode = seed.GetExitCodeAsync()

            if exitCode <> 0L then
                invalidOp "The fixture PostgreSQL seed did not shut down cleanly."

            return volume
        with error ->
            // The same Testcontainers session reaps the seed volume when the suite exits.
            // Remove it immediately if seeding fails.
            FixtureDisposal.afterFailure
                error
                [ fun () -> volume.DisposeAsync().AsTask().GetAwaiter().GetResult() ]

            return raise error
    }

let private seed = lazy (createSeed ())

let private seededFixture configure =
    task {
        let! volume = seed.Value

        return
            fixtureContainer (fun builder ->
                builder
                    .WithVolumeMount(volume.Name, seedMount, AccessMode.ReadOnly)
                    .WithEntrypoint(
                        "/bin/sh",
                        "-ec",
                        """
                        root=$1
                        seed=$2
                        shift 2
                        case "${PGDATA:?}" in
                            "$root"/*) source="$seed/${PGDATA#"$root"/}" ;;
                            *) echo 'Unexpected fixture PostgreSQL data directory.' >&2; exit 1 ;;
                        esac
                        test -s "$source/PG_VERSION"
                        test ! -e "$source/postmaster.pid"
                        mkdir -p "$PGDATA"
                        cp -a "$source/." "$PGDATA/"
                        exec /usr/local/bin/docker-entrypoint.sh "$@"
                        """,
                        "claimcore-seed-copy",
                        dataRoot,
                        seedMount
                    )
                |> configure)
    }

let private disposalSteps (container: PostgreSqlContainer) (sources: NpgsqlDataSource list) =
    [
        for source in sources do
            (fun () -> source.Dispose())

        (fun () -> container.DisposeAsync().AsTask().GetAwaiter().GetResult())
    ]

[<NoEquality; NoComparison>]
type private AdmittedConnections =
    {
        Owner: NpgsqlDataSource
        Superuser: NpgsqlDataSource
        Runtime: NpgsqlDataSource
    }

let private admittedDatabase
    (container: PostgreSqlContainer)
    sources
    (connections: AdmittedConnections)
    (installation: Installation)
    owner
    cipher
    masterKey
    keyFile
    keyDisposal
    =
    let ownerSource = connections.Owner
    let runtime = connections.Runtime

    let dispose () =
        disposalSteps container sources @ keyDisposal
        |> ClaimCore.TestSupport.FixtureDisposal.finish

    let fixture =
        {
            Store = LedgerStore(runtime, installation, cipher, Transactions.timeouts) :> IStore
            OperationIds = OperationIds.Scope()
            InstallationId = installation.InstallationId
            Owner = owner
            Calendar =
                ClaimCore.Server.Composition.BusinessCalendar.resolve installation.BusinessTimeZone
            Dispose = dispose
        }

    {
        Container = container
        OwnerSource = ownerSource
        SuperuserSource = connections.Superuser
        RuntimeSource = runtime
        MasterKey = masterKey
        Cipher = cipher
        DataKeyFile = keyFile
        Fixture = fixture
        Installation = installation
    }

let private createInZone seeded configure owner timeZone poolCapacity policy =
    task {
        let! container =
            if seeded && TestcontainersSettings.ResourceReaperEnabled then
                seededFixture configure
            else
                Task.FromResult(fixtureContainer configure)

        let mutable sources: NpgsqlDataSource list = []
        let mutable keyDisposal: (unit -> unit) list = []
        let mutable stage = SetupStage.ContainerReadiness
        let mutable ownedId = "CREATED_EVENT_NOT_OBSERVED"
        container.Created.AddHandler(EventHandler(fun _ _ -> ownedId <- container.Id))

        try
            do! container.StartAsync()
            stage <- SetupStage.DatabaseRoles
            let superuser = NpgsqlDataSource.Create(container.GetConnectionString())
            sources <- [ superuser ]
            do! createRoles superuser CancellationToken.None

            let ownerSource = NpgsqlDataSource.Create(ownerConnection container)
            sources <- ownerSource :: sources

            stage <- SetupStage.SchemaInitialization

            let owned = Directory.CreateTempSubdirectory "claimcore-data-key-"
            let keyFile = Path.Combine(owned.FullName, "data.key")

            keyDisposal <- [ (fun () -> File.Delete keyFile); (fun () -> owned.Delete()) ]

            let masterKey = writeDataKeyFile keyFile
            keyDisposal <- (fun () -> (masterKey :> IDisposable).Dispose()) :: keyDisposal

            let! installationId =
                task {
                    match policy with
                    | None ->
                        return!
                            SchemaAdministration.initialize
                                ownerSource
                                masterKey
                                timeZone
                                (fun _ _ _ -> Task.FromResult(()))
                                CancellationToken.None
                    | Some _ ->
                        use setup = new Administration(ownerConnection container, "test", masterKey)

                        let initial =
                            {
                                Principal = owner
                                Profile = DomainInputs.ownerProfile
                                Roles = ClaimCore.Domain.Tasks.Registry.initialOwnerRoles
                            }

                        let! installation =
                            setup.Initialize(timeZone, initial, policy, CancellationToken.None)

                        return installation.InstallationId
                }

            stage <- SetupStage.RuntimeAdmission
            let settings = runtimeSettings container
            poolCapacity |> Option.iter (fun capacity -> settings.MaxPoolSize <- capacity)
            let runtime = NpgsqlDataSource.Create(settings.ConnectionString)
            sources <- runtime :: sources

            let! admitted, cipher =
                match policy with
                | Some _ -> InstallationCheck.openRuntime runtime masterKey CancellationToken.None
                | None ->
                    // Schema-only tests provision the first owner themselves. They exercise storage
                    // directly, because real runtime admission requires sequence-one evidence.
                    DatabaseSession.read runtime CancellationToken.None (fun connection ->
                        task {
                            let! installation = Baseline.require connection CancellationToken.None

                            let! cipher =
                                Baseline.loadCipher
                                    connection
                                    installation.InstallationId
                                    masterKey
                                    CancellationToken.None

                            return installation, cipher
                        })

            keyDisposal <- (fun () -> (cipher :> IDisposable).Dispose()) :: keyDisposal

            if admitted.InstallationId <> installationId then
                invalidOp "InstallationCheck changed identity."

            return
                admittedDatabase
                    container
                    sources
                    {
                        Owner = ownerSource
                        Superuser = superuser
                        Runtime = runtime
                    }
                    admitted
                    owner
                    cipher
                    masterKey
                    keyFile
                    keyDisposal
        with error ->
            // A broken diagnostic sink must not bypass owned cleanup or replace setup failure.
            try
                printfn
                    "Owned PostgreSQL setup failed: container=%s stage=%A category=%s"
                    ownedId
                    stage
                    (failureCategory error)
            with
            | :? IO.IOException
            | :? ObjectDisposedException -> ()

            disposalSteps container sources @ keyDisposal
            |> ClaimCore.TestSupport.FixtureDisposal.afterFailure error

            return Unchecked.defaultof<Database>
    }

let private defaults builder = builder

let internal createFor principal =
    createInZone true defaults principal "Etc/UTC" None None

let internal createWithZone timeZone =
    createInZone true defaults owner timeZone None None

let internal createForPool principal capacity =
    createInZone true defaults principal "Etc/UTC" (Some capacity) None

let internal create () = createFor owner

let internal createWithReferencePolicy policy =
    createInZone true defaults owner "Etc/UTC" None (Some policy)

let internal createWithStatementCounts () =
    let configure (builder: PostgreSqlBuilder) =
        builder.WithCommand("-c", "shared_preload_libraries=pg_stat_statements")

    createInZone false configure owner "Etc/UTC" None None

/// The two additional ports belong to independent, promoted physical restores inside the
/// owned fixture. The primary archives to an atomic, test-only sibling of its data directory.
let internal createArchived () =
    let configure (builder: PostgreSqlBuilder) =
        builder
            .WithPortBinding(5433, true)
            .WithPortBinding(5434, true)
            .WithCommand(
                "-c",
                "wal_level=replica",
                "-c",
                "fsync=on",
                "-c",
                "full_page_writes=on",
                "-c",
                "synchronous_commit=on",
                "-c",
                "archive_mode=on",
                "-c",
                "archive_command=mkdir -p /var/lib/postgresql/attachment-wal && cp %p /var/lib/postgresql/attachment-wal/%f.tmp && mv /var/lib/postgresql/attachment-wal/%f.tmp /var/lib/postgresql/attachment-wal/%f"
            )

    createInZone false configure owner "Etc/UTC" None None

let internal fixture () =
    task { let! database = create () in return database.Fixture }
