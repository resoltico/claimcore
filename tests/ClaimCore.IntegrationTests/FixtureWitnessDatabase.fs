module internal ClaimCore.IntegrationTests.FixtureWitnessDatabase

open System.Threading
open System
open System.Security.Cryptography
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.FixtureEnvironment

[<NoEquality; NoComparison>]
type TestWitness =
    {
        Container: PostgreSqlContainer
        OwnerConnection: string
        WriterConnection: string
        AuditorConnection: string
        Key: byte array
        Capability: byte array
        KeyId: Guid
        Protocol: WitnessProtocol
    }

let private provision
    owner
    databaseName
    writerPassword
    auditPassword
    identity
    keyId
    (key: byte array)
    capability
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            $"CREATE ROLE claimcore_witness_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{writerPassword}'; CREATE ROLE claimcore_witness_auditor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{auditPassword}'; REVOKE ALL ON DATABASE {databaseName} FROM PUBLIC; GRANT CONNECT ON DATABASE {databaseName} TO claimcore_witness_writer, claimcore_witness_auditor;",
            connection
        )

    command.ExecuteNonQuery() |> ignore
    use custody = new KeyRing(keyId, [ keyId, key ]) :> IKeyCustody
    let check = KeyCheck.create custody identity.InstallationId identity.LineageId
    Baseline.initialize owner identity InstallationUseScope.SyntheticOnly keyId check capability

let private roleConnection (owner: string) (role: string) (password: string) =
    let builder = NpgsqlConnectionStringBuilder(owner)
    builder.Username <- role
    builder.Password <- password
    builder.PersistSecurityInfo <- false
    builder.LogParameters <- false
    builder.ConnectionString

let start (admin: string) (suffix: string) =
    let name = "claimcore_witness_" + suffix.Substring(0, 12) + "_test"
    let ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let writerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let auditPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
    let container = startContainer name "claimcore_witness_owner" ownerPassword
    let mutable key: byte array option = None
    let mutable capability: byte array option = None
    let mutable protocol: WitnessProtocol option = None

    try
        let owner = container.GetConnectionString()
        let identity = FixtureWitnessIdentity.read admin
        let keyId = Guid.NewGuid()
        let material = RandomNumberGenerator.GetBytes(32)
        let writerCapability = RandomNumberGenerator.GetBytes(32)
        key <- Some material
        capability <- Some writerCapability
        provision owner name writerPassword auditPassword identity keyId material writerCapability
        let writer = roleConnection owner "claimcore_witness_writer" writerPassword
        let auditor = roleConnection owner "claimcore_witness_auditor" auditPassword

        let active =
            new WitnessProtocol(
                new ClaimCore.Witness.Store(writer, identity, writerCapability),
                (new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody),
                identity
            )

        protocol <- Some active
        (active.Admit(CancellationToken.None).GetAwaiter().GetResult())

        {
            Container = container
            OwnerConnection = owner
            WriterConnection = writer
            AuditorConnection = auditor
            Key = material
            Capability = writerCapability
            KeyId = keyId
            Protocol = active
        }
    with _ ->
        protocol |> Option.iter (fun value -> (value :> IDisposable).Dispose())
        key |> Option.iter CryptographicOperations.ZeroMemory
        capability |> Option.iter CryptographicOperations.ZeroMemory
        tryDisposeContainer container
        reraise ()
