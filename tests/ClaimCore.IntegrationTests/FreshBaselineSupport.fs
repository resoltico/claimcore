module ClaimCore.IntegrationTests.FreshBaselineSupport

open ClaimCore.TestSupport
open System
open System.IO
open System.Security.Cryptography
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixtureEnvironment

let execute connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let scalar connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)

    command.ExecuteScalar()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Synthetic scalar is missing.")

let private quoteIdentifier value =
    use builder = new NpgsqlCommandBuilder()
    builder.QuoteIdentifier(value)

let private connectionFor database (source: string) =
    let builder = NpgsqlConnectionStringBuilder(source)
    builder.Database <- database
    builder.ConnectionString

// The fixture creates and owns this random isolated database; no developer volume is touched.
let withDatabase action =
    let database = "baseline_" + Guid.NewGuid().ToString("N") + "_test"
    let root = adminConnection ()
    let admin = connectionFor database root
    let app = connectionFor database (appConnection ())
    let quoted = quoteIdentifier database
    execute root ("CREATE DATABASE " + quoted)

    FixtureCleanup.run
        (fun () ->
            NpgsqlConnection.ClearAllPools()
            execute root ("DROP DATABASE " + quoted))
        (fun () ->
            execute
                root
                ("REVOKE ALL ON DATABASE "
                 + quoted
                 + " FROM PUBLIC; GRANT CONNECT ON DATABASE "
                 + quoted
                 + " TO claimcore_app")

            execute admin "REVOKE CREATE ON SCHEMA public FROM PUBLIC"
            action admin app)

let private installationIdentity primaryOwner =
    use connection = new NpgsqlConnection(primaryOwner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id,lineage_id,witness_epoch "
            + "FROM claimcore.installation_lineage WHERE singleton",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic primary identity is required."

    let identity: Identity =
        {
            InstallationId = reader.GetGuid(0)
            LineageId = reader.GetGuid(1)
            Epoch = reader.GetInt64(2)
        }

    identity

let private withWitnessCapability scope primaryOwner owner writer action =
    let identity = installationIdentity primaryOwner
    let keyId = Guid.NewGuid()
    let key = witnessKey ()
    let capability = RandomNumberGenerator.GetBytes(32)

    let directory =
        Path.GetDirectoryName(writerCapabilityFile ())
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Synthetic writer directory is missing.")

    let capabilityPath =
        Path.Combine(directory, "separate-writer-" + Guid.NewGuid().ToString("N") + ".cap")

    let previous =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")

    try
        privateBytes directory capabilityPath capability

        Environment.SetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE", capabilityPath)

        use custody = new KeyRing(keyId, [ keyId, key ]) :> IKeyCustody
        let check = KeyCheck.create custody identity.InstallationId identity.LineageId
        ClaimCore.Witness.Baseline.initialize owner identity scope keyId check capability
        action writer capability
    finally
        try
            Environment.SetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE", previous)

            File.Delete(capabilityPath)
        finally
            CryptographicOperations.ZeroMemory(key)
            CryptographicOperations.ZeroMemory(capability)

let withWitnessForScope scope primaryOwner action =
    let database = "witness_" + Guid.NewGuid().ToString("N") + "_test"
    let root = witnessOwnerConnection ()
    let owner = connectionFor database root
    let writer = connectionFor database (witnessConnection ())
    let quoted = quoteIdentifier database
    execute root ("CREATE DATABASE " + quoted)

    FixtureCleanup.run
        (fun () ->
            NpgsqlConnection.ClearAllPools()
            execute root ("DROP DATABASE " + quoted))
        (fun () ->
            execute
                root
                ("REVOKE ALL ON DATABASE "
                 + quoted
                 + " FROM PUBLIC; GRANT CONNECT ON DATABASE "
                 + quoted
                 + " TO claimcore_witness_writer, claimcore_witness_auditor")

            withWitnessCapability scope primaryOwner owner writer action)

let withWitnessFor primaryOwner action =
    withWitnessForScope InstallationUseScope.SyntheticOnly primaryOwner action

let initialize admin =
    SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
    |> completedAdministration

let runtimeRefuses (app: string) =
    use source = RuntimeDataSource.create app

    Expect.throwsT<RuntimeDatabaseMismatch>
        (fun () -> use _connection = RuntimeDatabase.openConnection source in ())
        "The runtime refuses unsupported storage before serving case work"

let private tableNames connectionString =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
            + "WHERE n.nspname='claimcore' AND c.relkind='r' ORDER BY c.relname",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0)
    ]

let private catalogSql =
    """
        SELECT coalesce(jsonb_agg(jsonb_build_array(c.relname,c.relkind,c.relowner,c.relacl::text,
            (SELECT jsonb_agg(jsonb_build_array(a.attname,a.atttypid,a.attnotnull) ORDER BY a.attnum)
             FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
            (SELECT jsonb_agg(pg_get_constraintdef(k.oid) ORDER BY k.conname)
             FROM pg_catalog.pg_constraint k WHERE k.conrelid=c.oid)) ORDER BY c.relname),'[]'::jsonb)::text
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='claimcore'
            """

let snapshot admin =
    let catalog = scalar admin catalogSql :?> string

    let rows =
        tableNames admin
        |> List.map (fun name ->
            let sql =
                "SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM claimcore."
                + quoteIdentifier name
                + " r"

            name, scalar admin sql :?> string)

    catalog, rows

let assertUnsupported admin app =
    let before = snapshot admin

    SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    SchemaBaseline.verify admin
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    PreparationPruning.prune admin PreparationPruneOptions.defaults
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    runtimeRefuses app
    Expect.equal (snapshot admin) before "Refusal changes neither DDL, grants nor existing rows"

let assertRemoteOwnerTransport (builder: NpgsqlConnectionStringBuilder) =
    builder.Options <- ""
    builder.Host <- "database.example.invalid"

    for mode in [ SslMode.Disable; SslMode.Prefer; SslMode.Require; SslMode.VerifyCA ] do
        builder.SslMode <- mode

        SchemaBaseline.verify builder.ConnectionString
        |> refusedAdministration AdministrationFailure.OwnerConnectionInvalid

    builder.SslMode <- SslMode.VerifyFull
    let verified = OwnerConnection.builder builder.ConnectionString

    Expect.equal
        verified.GssEncryptionMode
        GssEncryptionMode.Disable
        "A remote owner connection must use authenticated TLS rather than GSS fallback"

    Expect.isTrue verified.CheckCertificateRevocation "Remote owner TLS checks revocation"
