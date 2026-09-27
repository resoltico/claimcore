module ClaimCore.IntegrationTests.FixtureDatabase

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Expecto
open Testcontainers.PostgreSql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.FixtureEnvironment
open ClaimCore.IntegrationTests.FixtureWitnessDatabase

let completedAdministration outcome =
    match outcome with
    | AdministrationOutcome.Completed value -> value
    | AdministrationOutcome.NotStarted _
    | AdministrationOutcome.NotCommitted _ ->
        failtest "Expected confirmed administrative completion."
    | _ -> failtest "Expected confirmed administrative completion."

let refusedAdministration expected outcome =
    match outcome with
    | AdministrationOutcome.NotStarted reason
    | AdministrationOutcome.NotCommitted reason ->
        Expect.equal reason expected "Exact administrative refusal"
    | _ -> failtest "Expected definite administrative refusal."

let accepted result =
    match result with
    | Ok value -> value
    | Error _ -> failtest "Expected acceptance."

let await (operation: Task<'value>) = operation.GetAwaiter().GetResult()

let syntheticSuppressionCheck = FixturePrivateFiles.syntheticSuppressionCheck

[<NoEquality; NoComparison>]
type private TestDatabase =
    {
        Container: PostgreSqlContainer
        WitnessContainer: PostgreSqlContainer
        AdminConnection: string
        AppConnection: string
        WitnessConnection: string
        WitnessAuditConnection: string
        WitnessOwnerConnection: string
        WitnessKey: byte array
        WriterCapability: byte array
        WitnessKeyId: Guid
        DataSource: NpgsqlDataSource
        WitnessProtocol: WitnessProtocol
        AppConnectionFile: string
        SuppressionKeyFile: string
        ArtifactKeyRingFile: string
        WriterCapabilityFile: string
        PreviousCapabilityFile: string option
        TemporaryDirectory: string
    }

let private cleanupGate = obj ()
let mutable private cleaned = false

let private cleanup (database: TestDatabase) =
    lock cleanupGate (fun () ->
        if not cleaned then
            let failures = ResizeArray<exn>()

            try
                (database.WitnessProtocol :> IDisposable).Dispose()
                database.DataSource.Dispose()
            with error ->
                failures.Add(error)

            try
                disposeContainer database.Container
            with error ->
                failures.Add(error)

            try
                disposeContainer database.WitnessContainer
            with error ->
                failures.Add(error)

            Array.Clear(database.WitnessKey)
            CryptographicOperations.ZeroMemory(database.WriterCapability)

            Environment.SetEnvironmentVariable(
                "CLAIMCORE_WRITER_CAPABILITY_FILE",
                database.PreviousCapabilityFile |> Option.toObj
            )

            try
                Directory.Delete(database.TemporaryDirectory, true)
            with error ->
                failures.Add(error)

            if failures.Count = 0 then
                cleaned <- true
            else
                raise (AggregateException("Integration fixture cleanup failed.", failures)))

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

    SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
    |> completedAdministration

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

let private registerProcessCleanup result =
    AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        try
            cleanup result
        with _ ->
            ())

let private assembledDatabase
    (container: PostgreSqlContainer)
    admin
    application
    (active: TestWitness)
    (source: NpgsqlDataSource)
    suffix
    previousCapabilityFile
    =
    let directory, file, suppressionFile, artifactFile, capabilityFile =
        FixturePrivateFiles.create suffix application active.Capability

    Environment.SetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE", capabilityFile)

    {
        Container = container
        WitnessContainer = active.Container
        AdminConnection = admin
        AppConnection = application
        WitnessConnection = active.WriterConnection
        WitnessAuditConnection = active.AuditorConnection
        WitnessOwnerConnection = active.OwnerConnection
        WitnessKey = active.Key
        WriterCapability = active.Capability
        WitnessKeyId = active.KeyId
        DataSource = source
        WitnessProtocol = active.Protocol
        AppConnectionFile = file
        SuppressionKeyFile = suppressionFile
        ArtifactKeyRingFile = artifactFile
        WriterCapabilityFile = capabilityFile
        PreviousCapabilityFile = previousCapabilityFile
        TemporaryDirectory = directory
    }

let private createTestDatabase () =
    let previousCapabilityFile =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj

    let suffix = Guid.NewGuid().ToString("N")
    let ownerName = "cc_owner_" + suffix.Substring(0, 12)
    let databaseName = "claimcore_" + suffix.Substring(12, 12) + "_test"
    let ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let appPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let container = startContainer databaseName ownerName ownerPassword
    let mutable witness: TestWitness option = None
    let mutable dataSource: NpgsqlDataSource option = None

    try
        let admin = container.GetConnectionString()
        provision admin databaseName appPassword
        let application = runtimeConnection admin appPassword
        let active = FixtureWitnessDatabase.start admin suffix
        witness <- Some active
        let source = RuntimeDataSource.create application
        dataSource <- Some source

        let result =
            assembledDatabase
                container
                admin
                application
                active
                source
                suffix
                previousCapabilityFile

        registerProcessCleanup result

        result
    with _ ->
        Environment.SetEnvironmentVariable(
            "CLAIMCORE_WRITER_CAPABILITY_FILE",
            previousCapabilityFile |> Option.toObj
        )

        dataSource |> Option.iter _.Dispose()

        witness
        |> Option.iter (fun value ->
            (value.Protocol :> IDisposable).Dispose()
            Array.Clear(value.Key)
            CryptographicOperations.ZeroMemory(value.Capability)
            tryDisposeContainer value.Container)

        tryDisposeContainer container
        reraise ()

let private database = lazy (createTestDatabase ())

let private currentDatabase () = database.Value
let internal dataSource () = (currentDatabase ()).DataSource
let appConnection () = (currentDatabase ()).AppConnection
let adminConnection () = (currentDatabase ()).AdminConnection
let witnessConnection () = (currentDatabase ()).WitnessConnection

let witnessAuditConnection () =
    (currentDatabase ()).WitnessAuditConnection

let internal containerIds () =
    let current = currentDatabase ()
    current.Container.Id, current.WitnessContainer.Id

let witnessOwnerConnection () =
    (currentDatabase ()).WitnessOwnerConnection

let witnessKey () =
    Array.copy (currentDatabase ()).WitnessKey

let witnessKeyId () = (currentDatabase ()).WitnessKeyId
let internal witnessProtocol () = (currentDatabase ()).WitnessProtocol

let witnessStore writer identity =
    new ClaimCore.Witness.Store(writer, identity, (currentDatabase ()).WriterCapability)

let suppressionKeyFile () = (currentDatabase ()).SuppressionKeyFile

let artifactKeyRingFile () =
    (currentDatabase ()).ArtifactKeyRingFile

let writerCapabilityFile () =
    (currentDatabase ()).WriterCapabilityFile

let witnessedOpen primary cancellation =
    Runtime.OpenPostgres(
        primary,
        witnessConnection (),
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        cancellation
    )

let appConnectionFile () = (currentDatabase ()).AppConnectionFile

/// Test adapters with an explicit assembly teardown hook can call this; ProcessExit/Ryuk remain fallbacks.
let shutdown () =
    if database.IsValueCreated then
        cleanup database.Value
