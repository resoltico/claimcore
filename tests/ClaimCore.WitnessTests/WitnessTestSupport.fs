module internal ClaimCore.WitnessTests.WitnessTestSupport

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open Expecto
open Testcontainers.PostgreSql
open ClaimCore.Witness
open ClaimCore.TestSupport

let image =
    use baseline =
        System.Text.Json.JsonDocument.Parse(
            System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(
                    ClaimCore.TestSupport.RepositoryRoot.find (),
                    "db/postgresql-baseline.json"
                )
            )
        )

    baseline.RootElement.GetProperty("containerImage").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "The baseline containerImage must be text.")

let await (work: System.Threading.Tasks.Task<'value>) = work.GetAwaiter().GetResult()

let cancellation = CancellationToken.None

let keyId = Guid.Parse("7f271edd-e72d-4147-9c71-570845ff6f95")

let assertRemoteTransport (writer: string) (identity: Identity) (capability: byte array) =
    let remote = NpgsqlConnectionStringBuilder(writer)
    remote.Host <- "witness.example.invalid"
    remote.SslMode <- SslMode.Prefer

    Expect.throwsT<ArgumentException>
        (fun () -> use _refused = new Store(remote.ConnectionString, identity, capability) in ())
        "Witness writer refuses an unauthenticated remote endpoint before network access"

    remote.SslMode <- SslMode.VerifyFull

    let verified =
        NpgsqlConnectionStringBuilder(PostgresTransport.connectionString remote.ConnectionString)

    Expect.equal
        verified.GssEncryptionMode
        GssEncryptionMode.Disable
        "Verified TLS disables GSS fallback"

    Expect.isTrue
        verified.CheckCertificateRevocation
        "Verified remote TLS checks certificate revocation"

let run (connection: string) sql =
    use db = new NpgsqlConnection(connection)
    db.Open()
    use command = new NpgsqlCommand(sql, db)
    command.ExecuteNonQuery() |> ignore

let scalar<'a> (connection: string) sql =
    use db = new NpgsqlConnection(connection)
    db.Open()
    use command = new NpgsqlCommand(sql, db)
    command.ExecuteScalar() :?> 'a

let createContainer password =
    PostgreSqlBuilder(image)
        .WithDatabase("witness_synthetic")
        .WithUsername("claimcore_witness_owner")
        .WithPassword(password)
        .WithCommand("-c", "fsync=on")
        .WithCommand("-c", "full_page_writes=on")
        .WithCommand("-c", "synchronous_commit=on")
        .Build()


let private provision owner capability test =
    let writerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let auditPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))

    run
        owner
        ($"CREATE ROLE claimcore_witness_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{writerPassword}';")

    run
        owner
        ($"CREATE ROLE claimcore_witness_auditor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{auditPassword}';")

    run
        owner
        "REVOKE ALL ON DATABASE witness_synthetic FROM PUBLIC; GRANT CONNECT ON DATABASE witness_synthetic TO claimcore_witness_writer, claimcore_witness_auditor;"

    let identity =
        {
            InstallationId = Guid.NewGuid()
            LineageId = Guid.NewGuid()
            Epoch = 1L
        }

    let key = RandomNumberGenerator.GetBytes(32)
    use custody = new KeyRing(keyId, [ keyId, key ]) :> IKeyCustody
    let check = KeyCheck.create custody identity.InstallationId identity.LineageId
    Baseline.initialize owner identity InstallationUseScope.SyntheticOnly keyId check capability
    let builder = NpgsqlConnectionStringBuilder(owner)
    builder.Username <- "claimcore_witness_writer"
    builder.Password <- writerPassword
    builder.PersistSecurityInfo <- false
    builder.LogParameters <- false
    let auditor = NpgsqlConnectionStringBuilder(builder.ConnectionString)
    auditor.Username <- "claimcore_witness_auditor"
    auditor.Password <- auditPassword
    test owner builder.ConnectionString auditor.ConnectionString identity capability

let fixtureRoles test =
    let password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let container = createContainer password
    let capability = RandomNumberGenerator.GetBytes(32)

    FixtureCleanup.run
        (fun () ->
            CryptographicOperations.ZeroMemory(capability)
            container.DisposeAsync().AsTask().GetAwaiter().GetResult())
        (fun () ->
            let mutable selected = None
            let mutable phase = "startup"

            try
                container.StartAsync().GetAwaiter().GetResult()
                let owner = container.GetConnectionString()
                selected <- Some owner
                phase <- "owner-provision"

                provision owner capability (fun owner writer auditor identity capability ->
                    phase <- "test-body"
                    test owner writer auditor identity capability)
            with error ->
                if phase <> "test-body" then
                    WitnessFixtureDiagnostics.capture container selected password phase error

                reraise ())

let fixture test =
    fixtureRoles (fun owner writer _ identity capability -> test owner writer identity capability)

let payload (value: byte) = [| 0x43uy; 0x43uy; 0x57uy; value |]
