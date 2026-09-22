module ClaimCore.IntegrationTests.Fixtures

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Expecto
open Testcontainers.PostgreSql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres

let completedAdministration outcome =
    match outcome with
    | AdministrationOutcome.Completed value -> value
    | result -> failtestf "Expected confirmed administrative completion, got %A" result

let refusedAdministration expected outcome =
    match outcome with
    | AdministrationOutcome.NotStarted reason
    | AdministrationOutcome.NotCommitted reason ->
        Expect.equal reason expected "Exact administrative refusal"
    | result -> failtestf "Expected definite administrative refusal, got %A" result

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let await (operation: Task<'value>) = operation.GetAwaiter().GetResult()

[<NoEquality; NoComparison>]
type private TestDatabase =
    {
        Container: PostgreSqlContainer
        AdminConnection: string
        AppConnection: string
        AppConnectionFile: string
        TemporaryDirectory: string
    }

let private disposeContainer (container: PostgreSqlContainer) =
    container.DisposeAsync().AsTask().GetAwaiter().GetResult()

let private tryDisposeContainer (container: PostgreSqlContainer) =
    try
        disposeContainer container
    with _ ->
        ()

let private cleanupGate = obj ()
let mutable private cleaned = false

let private cleanup (database: TestDatabase) =
    lock cleanupGate (fun () ->
        if not cleaned then
            let failures = ResizeArray<exn>()

            try
                disposeContainer database.Container
            with error ->
                failures.Add(error)

            try
                Directory.Delete(database.TemporaryDirectory, true)
            with error ->
                failures.Add(error)

            if failures.Count = 0 then
                cleaned <- true
            else
                raise (AggregateException("Integration fixture cleanup failed.", failures)))

let private testRunLabel () =
    match Environment.GetEnvironmentVariable("CLAIMCORE_TEST_RUN_LABEL") with
    | null
    | "" -> $"claimcore-integration-local-{Environment.ProcessId}-{Guid.NewGuid():N}"
    | value when Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$") -> value
    | _ -> invalidOp "CLAIMCORE_TEST_RUN_LABEL must be a bounded portable Docker label value."

let private privateFile (directory: string) (path: string) (value: string) =
    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    use stream = new FileStream(path, options)
    use writer = new StreamWriter(stream, UTF8Encoding(false, true))
    writer.Write(value)
    writer.Flush()
    stream.Flush(true)

let private startContainer (databaseName: string) (ownerName: string) (ownerPassword: string) =
    let container =
        PostgreSqlBuilder(Baseline.containerImage)
            .WithDatabase(databaseName)
            .WithUsername(ownerName)
            .WithPassword(ownerPassword)
            .WithLabel("org.claimcore.test-run", testRunLabel ())
            // The Testcontainers module disables durability for speed by default; this product requires it.
            .WithCommand("-c", "fsync=on")
            .WithCommand("-c", "full_page_writes=on")
            .WithCommand("-c", "synchronous_commit=on")
            .Build()

    try
        container.StartAsync().GetAwaiter().GetResult()
        container
    with _ ->
        tryDisposeContainer container
        reraise ()

let private provision (admin: string) (databaseName: string) (appPassword: string) =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    // All interpolated values are generated lowercase ASCII/hex here, never external input.
    use command =
        new NpgsqlCommand(
            $"""
            CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
                NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{appPassword}';
            REVOKE ALL ON DATABASE {databaseName} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {databaseName} TO claimcore_app;
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            """,
            connection
        )

    command.ExecuteNonQuery() |> ignore
    Migrations.apply admin |> completedAdministration
    InstallationBusinessZone.set admin "Etc/UTC" |> completedAdministration

let private runtimeConnection (admin: string) (appPassword: string) =
    let application = NpgsqlConnectionStringBuilder(admin)
    application.Username <- "claimcore_app"
    application.Password <- appPassword
    application.IncludeErrorDetail <- false
    application.LogParameters <- false
    application.PersistSecurityInfo <- false
    application.NoResetOnClose <- false
    application.Enlist <- false
    application.ConnectionString

let private createTestDatabase () =
    let suffix = Guid.NewGuid().ToString("N")
    let ownerName = "cc_owner_" + suffix.Substring(0, 12)
    let databaseName = "claimcore_" + suffix.Substring(12, 12) + "_test"
    let ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let appPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let container = startContainer databaseName ownerName ownerPassword

    try
        let admin = container.GetConnectionString()
        provision admin databaseName appPassword
        let application = runtimeConnection admin appPassword
        let directory = Path.Combine(Path.GetTempPath(), "claimcore-integration-" + suffix)
        let file = Path.Combine(directory, "application.connection")
        privateFile directory file application

        let result =
            {
                Container = container
                AdminConnection = admin
                AppConnection = application
                AppConnectionFile = file
                TemporaryDirectory = directory
            }

        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                cleanup result
            with _ ->
                ())

        result
    with _ ->
        tryDisposeContainer container
        reraise ()

let private database = lazy (createTestDatabase ())

let private currentDatabase () = database.Value
let appConnection () = (currentDatabase ()).AppConnection
let adminConnection () = (currentDatabase ()).AdminConnection
let appConnectionFile () = (currentDatabase ()).AppConnectionFile

/// Test adapters with an explicit assembly teardown hook can call this; ProcessExit/Ryuk remain fallbacks.
let shutdown () =
    if database.IsValueCreated then
        cleanup database.Value

let internal store () =
    let value = new PostgresStore(appConnection ())
    value.CheckSchema() |> await |> accepted
    value

let internal clock =
    { new IBusinessTime with
        member _.Capture() =
            {
                ObservedUtcInstant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
                EffectiveBusinessDate = DateOnly(2026, 9, 7)
                TimeZoneId = "Etc/UTC"
            }
    }

let internal recoveryPageLimit = SemanticContract.current.MaximumPageSize

let registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "Lithuania"
        ClaimantName = "Integration Test Company"
        InsurerName = "Alleged Test Insurer"
        ClaimedAmount = "1000"
        ClaimedCurrency = "EUR"
    }

let newRequest () : CommandRequest =
    {
        OperationId = Guid.NewGuid()
        CaseReference = "TEST-" + Guid.NewGuid().ToString("N")
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

/// Native tests cross the core boundary with a closed domain request, never an adapter draft.
let openRequest operationId caseReference : CommandRequest =
    {
        OperationId = operationId
        CaseReference = caseReference
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let next (previous: CommandRequest) version command =
    { previous with
        OperationId = Guid.NewGuid()
        ExpectedVersion = version
        Command = command
    }

let runSql connectionString sql reference =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    let parameter = command.Parameters.Add("reference", NpgsqlDbType.Text)
    parameter.Value <- reference
    command.ExecuteNonQuery() |> ignore

/// Both independently supplied connections must name the same explicit test endpoint.
/// This catches setup mistakes, not malicious routing or proof that all existing data is synthetic.
let requireSameTarget (application: string) (owner: string) =
    let app = NpgsqlConnectionStringBuilder(application)
    let admin = NpgsqlConnectionStringBuilder(owner)
    let host = app.Host |> Option.ofObj |> Option.defaultValue ""
    let database = app.Database |> Option.ofObj |> Option.defaultValue ""
    let appUsername = app.Username |> Option.ofObj |> Option.defaultValue ""
    let adminHost = admin.Host |> Option.ofObj |> Option.defaultValue ""
    let adminDatabase = admin.Database |> Option.ofObj |> Option.defaultValue ""
    let adminUsername = admin.Username |> Option.ofObj |> Option.defaultValue ""

    if
        String.IsNullOrWhiteSpace(host)
        || host.Contains(',')
        || not (String.Equals(host, adminHost, StringComparison.OrdinalIgnoreCase))
        || app.Port <> admin.Port
        || database <> adminDatabase
        || String.IsNullOrWhiteSpace(database)
        || not (database.EndsWith("_test", StringComparison.Ordinal))
        || appUsername <> "claimcore_app"
        || String.IsNullOrWhiteSpace(adminUsername)
        || adminUsername = appUsername
    then
        invalidOp
            "Integration connections must name one explicit identical test host/port/database with distinct runtime and owner roles."

let validateTargets () =
    requireSameTarget (appConnection ()) (adminConnection ())
