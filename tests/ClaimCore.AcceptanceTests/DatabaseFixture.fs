module ClaimCore.AcceptanceTests.DatabaseFixture

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open Testcontainers.PostgreSql

[<NoEquality; NoComparison>]
type Context =
    {
        Inputs: Configuration.Inputs
        Container: PostgreSqlContainer
        AdminConnection: string
        ApplicationConnection: string
        TemporaryDirectory: string
        ApplicationConnectionFile: string
        CliDll: string
        DatabaseDll: string
        TreeDigests: string * string
    }

let private disposeContainer (container: PostgreSqlContainer) =
    try
        container.DisposeAsync().AsTask().GetAwaiter().GetResult()
    with _ ->
        ()

let private privateFile (directory: string) (name: string) (value: string) =
    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    let path = Path.Combine(directory, name)

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
    path

let private baselineImage (repositoryRoot: string) =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(repositoryRoot, "db/postgresql-baseline.json"))
        )

    match document.RootElement.GetProperty("containerImage").GetString() with
    | null -> invalidOp "PostgreSQL baseline image is null."
    | image -> image

let private provision (admin: string) (databaseName: string) (appPassword: string) =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

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

let private applicationConnection (admin: string) (appPassword: string) =
    let builder = NpgsqlConnectionStringBuilder(admin)
    builder.Username <- "claimcore_app"
    builder.Password <- appPassword
    builder.IncludeErrorDetail <- false
    builder.LogParameters <- false
    builder.PersistSecurityInfo <- false
    builder.Enlist <- false
    builder.ConnectionString

let private verifiedArtifacts (inputs: Configuration.Inputs) =
    let cliTree =
        PublishManifest.verify "publish-cli" inputs.CliDirectory inputs.CliManifest

    let databaseTree =
        PublishManifest.verify "publish-database" inputs.DatabaseDirectory inputs.DatabaseManifest

    let cliDll = Path.Combine(inputs.CliDirectory, "ClaimCore.Cli.dll")
    let databaseDll = Path.Combine(inputs.DatabaseDirectory, "ClaimCore.Database.dll")

    if not (File.Exists(cliDll) && File.Exists(databaseDll)) then
        invalidOp "Verified publish trees lack their required entry assemblies."

    cliDll, databaseDll, cliTree, databaseTree

let private testRunLabel (suffix: string) =
    match Environment.GetEnvironmentVariable("CLAIMCORE_TEST_RUN_LABEL") with
    | null
    | "" -> "claimcore-acceptance-" + suffix
    | value when Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$") -> value
    | _ -> invalidOp "CLAIMCORE_TEST_RUN_LABEL must be a bounded portable Docker label value."

let private newContainer (repositoryRoot: string) (suffix: string) =
    let databaseName = "claimcore_" + suffix.Substring(0, 12) + "_test"
    let owner = "cc_owner_" + suffix.Substring(12, 12)
    let ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let appPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let label = testRunLabel suffix

    let container =
        PostgreSqlBuilder(baselineImage repositoryRoot)
            .WithDatabase(databaseName)
            .WithUsername(owner)
            .WithPassword(ownerPassword)
            .WithLabel("org.claimcore.test-run", label)
            .WithCommand("-c", "fsync=on")
            .WithCommand("-c", "full_page_writes=on")
            .WithCommand("-c", "synchronous_commit=on")
            .Build()

    container, databaseName, appPassword

let private physicalTemporaryBase () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (temporary.StartsWith("/var/", StringComparison.Ordinal)
            || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + temporary
    else
        temporary

let private create () =
    let inputs = Configuration.load ()
    let cliDll, databaseDll, cliTree, databaseTree = verifiedArtifacts inputs
    let suffix = Guid.NewGuid().ToString("N")
    let container, databaseName, appPassword = newContainer inputs.RepositoryRoot suffix

    try
        container.StartAsync().GetAwaiter().GetResult()
        let admin = container.GetConnectionString()
        provision admin databaseName appPassword
        let application = applicationConnection admin appPassword

        let directory =
            Path.Combine(physicalTemporaryBase (), "claimcore-acceptance-" + suffix)

        let adminFile = privateFile directory "owner.connection" admin
        let applicationFile = privateFile directory "application.connection" application
        let environment = Dictionary<string, string>()
        environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- adminFile
        environment.Remove("CLAIMCORE_CONNECTION_FILE") |> ignore

        let migration =
            ProcessRunner.dotnet 120_000 databaseDll [ "migrate" ] environment None

        if migration.ExitCode <> 0 then
            invalidOp "Published database migration failed."

        let businessZone =
            ProcessRunner.dotnet
                120_000
                databaseDll
                [ "set-business-zone"; "Etc/UTC" ]
                environment
                None

        if businessZone.ExitCode <> 0 then
            invalidOp "Published database business-time-zone configuration failed."

        {
            Inputs = inputs
            Container = container
            AdminConnection = admin
            ApplicationConnection = application
            TemporaryDirectory = directory
            ApplicationConnectionFile = applicationFile
            CliDll = cliDll
            DatabaseDll = databaseDll
            TreeDigests = cliTree, databaseTree
        }
    with _ ->
        disposeContainer container
        reraise ()

let private context = lazy (create ())
let private gate = obj ()
let mutable private cleaned = false

let current () = context.Value

let shutdown () =
    lock gate (fun () ->
        if context.IsValueCreated && not cleaned then
            cleaned <- true
            let value = context.Value
            disposeContainer value.Container

            try
                Directory.Delete(value.TemporaryDirectory, true)
            with _ ->
                ())
