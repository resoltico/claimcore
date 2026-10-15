module ClaimCore.IntegrationTests.PostgresFixture

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private image () =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()

    use document =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "db/postgresql-baseline.json")))

    let baseline =
        document.RootElement.GetProperty("supportedMajors").EnumerateArray() |> Seq.head

    baseline.GetProperty("containerImage").GetString() |> nonNull

let private owner =
    PrincipalKey.human "https://issuer.example/" "synthetic-owner"
    |> Result.defaultWith invalidOp

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
        (new PostgreSqlBuilder(image ()))
            .WithLabel("claimcore.test", "ledger-store")
            .WithLabel("claimcore.test-run", Guid.NewGuid().ToString("D"))
            .WithTmpfsMount("/var/lib/postgresql")
            .Build()

    value

let internal empty () = container ()

let internal create () =
    task {
        let container = container ()
        let mutable sources: NpgsqlDataSource list = []

        try
            do! container.StartAsync()
            let ownerSource = NpgsqlDataSource.Create(container.GetConnectionString())
            sources <- [ ownerSource ]
            use! connection = ownerSource.OpenConnectionAsync()

            let! _ =
                DatabaseSession.execute
                    connection
                    "CREATE ROLE claimcore_runtime LOGIN PASSWORD 'synthetic-only'"
                    [||]
                    CancellationToken.None

            let! installationId =
                SchemaAdministration.initialize ownerSource "Etc/UTC" CancellationToken.None

            let settings = NpgsqlConnectionStringBuilder(container.GetConnectionString())
            settings.Username <- "claimcore_runtime"
            settings.Password <- "synthetic-only"
            let runtime = NpgsqlDataSource.Create(settings.ConnectionString)
            sources <- runtime :: sources
            let! admitted = Admission.openRuntime runtime CancellationToken.None

            if admitted.InstallationId <> installationId then
                invalidOp "Admission changed identity."

            let dispose () =
                for source in sources do
                    source.Dispose()

                container.DisposeAsync().AsTask().GetAwaiter().GetResult()

            let fixture =
                {
                    Store =
                        LedgerStore(runtime, installationId, Transactions.timeouts) :> ILedgerStore
                    InstallationId = installationId
                    Owner = owner
                    Calendar = calendar
                    Dispose = dispose
                }

            return
                {
                    Container = container
                    OwnerSource = ownerSource
                    RuntimeSource = runtime
                    Fixture = fixture
                }
        with error ->
            for source in sources do
                source.Dispose()

            do! container.DisposeAsync().AsTask()
            Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
            return Unchecked.defaultof<Database>
    }

let internal fixture () =
    task { let! database = create () in return database.Fixture }
