module ClaimCore.TestSupport.PostgresFixture

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures


type private SetupStage =
    | ContainerReadiness
    | OwnerConnection
    | RuntimeRole
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

let private calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"

        member _.DateAt instant =
            DateOnly.FromDateTime instant.UtcDateTime
    }

[<NoEquality; NoComparison>]
type internal Database =
    {
        Container: PostgreSqlContainer
        OwnerSource: NpgsqlDataSource
        RuntimeSource: NpgsqlDataSource
        Fixture: Fixture
    }

let private container () =
    let value =
        (new PostgreSqlBuilder(ClaimCore.TestSupport.PostgresBaseline.image ()))
            .WithLabel("claimcore.test", "ledger-store")
            .WithLabel("claimcore.test-run", Guid.NewGuid().ToString("D"))
            .WithTmpfsMount("/var/lib/postgresql")
            .WithWaitStrategy(ClaimCore.TestSupport.PostgresReadiness.strategy ())
            .Build()

    value

let internal empty () = container ()

let private disposalSteps (container: PostgreSqlContainer) (sources: NpgsqlDataSource list) =
    [
        for source in sources do
            (fun () -> source.Dispose())

        (fun () -> container.DisposeAsync().AsTask().GetAwaiter().GetResult())
    ]

let private admittedDatabase
    (container: PostgreSqlContainer)
    sources
    ownerSource
    runtime
    installationId
    owner
    =
    let dispose () =
        disposalSteps container sources |> ClaimCore.TestSupport.FixtureDisposal.finish

    let fixture =
        {
            Store = LedgerStore(runtime, installationId, Transactions.timeouts) :> IStore
            InstallationId = installationId
            Owner = owner
            Calendar = calendar
            Dispose = dispose
        }

    {
        Container = container
        OwnerSource = ownerSource
        RuntimeSource = runtime
        Fixture = fixture
    }

let internal createFor owner =
    task {
        let container = container ()
        let mutable sources: NpgsqlDataSource list = []
        let mutable stage = SetupStage.ContainerReadiness
        let mutable ownedId = "CREATED_EVENT_NOT_OBSERVED"
        container.Created.AddHandler(EventHandler(fun _ _ -> ownedId <- container.Id))

        try
            do! container.StartAsync()
            stage <- SetupStage.OwnerConnection
            let ownerSource = NpgsqlDataSource.Create(container.GetConnectionString())
            sources <- [ ownerSource ]
            use! connection = ownerSource.OpenConnectionAsync()

            stage <- SetupStage.RuntimeRole

            let! _ =
                DatabaseSession.execute
                    connection
                    "CREATE ROLE claimcore_runtime LOGIN PASSWORD 'synthetic-only'"
                    [||]
                    CancellationToken.None

            stage <- SetupStage.SchemaInitialization

            let! installationId =
                SchemaAdministration.initialize ownerSource "Etc/UTC" CancellationToken.None

            stage <- SetupStage.RuntimeAdmission
            let settings = NpgsqlConnectionStringBuilder(container.GetConnectionString())
            settings.Username <- "claimcore_runtime"
            settings.Password <- "synthetic-only"
            let runtime = NpgsqlDataSource.Create(settings.ConnectionString)
            sources <- runtime :: sources
            let! admitted = InstallationCheck.openRuntime runtime CancellationToken.None

            if admitted.InstallationId <> installationId then
                invalidOp "InstallationCheck changed identity."

            return admittedDatabase container sources ownerSource runtime installationId owner
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

            disposalSteps container sources
            |> ClaimCore.TestSupport.FixtureDisposal.afterFailure error

            return Unchecked.defaultof<Database>
    }

let internal create () = createFor owner

let internal fixture () =
    task { let! database = create () in return database.Fixture }
