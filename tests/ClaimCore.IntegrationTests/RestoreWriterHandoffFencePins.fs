module internal ClaimCore.IntegrationTests.RestoreWriterHandoffFencePins

open System
open System.Security.Cryptography
open System.Text
open Expecto
open Npgsql
open ClaimCore.IntegrationTests.RestorePhysicalConnections

[<NoEquality; NoComparison>]
type WriterFencePins =
    {
        OldEndpointId: Guid
        OldEndpointAddressSha256: string
        OldPrimaryRoleOid: int64
        OldWitnessRoleOid: int64
        OldPrimaryCredentialSha256: string
        OldWitnessCredentialSha256: string
        PrimarySessionSetSha256: string
        WitnessSessionSetSha256: string
    }

let private digest (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let private roleFacts (connectionString: string) (role: string) =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use identity =
        new NpgsqlCommand("SELECT oid::bigint FROM pg_roles WHERE rolname=@role", connection)

    identity.Parameters.AddWithValue("role", role) |> ignore
    let oid = identity.ExecuteScalar() :?> int64

    if oid < 1L then
        failtest "Old writer role identity is invalid"

    use sessions =
        new NpgsqlCommand(
            "SELECT pid FROM pg_stat_activity WHERE usename=@role ORDER BY pid",
            connection
        )

    sessions.Parameters.AddWithValue("role", role) |> ignore
    use reader = sessions.ExecuteReader()
    let pids = ResizeArray<int>()

    while reader.Read() do
        pids.Add(reader.GetInt32(0))

    let sessionBytes = String.Join("\n", pids) + "\n" |> Encoding.ASCII.GetBytes
    oid, digest sessionBytes

let private credentialDigest (connectionString: string) =
    let builder = NpgsqlConnectionStringBuilder(connectionString)

    let password =
        builder.Password
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Old synthetic writer credential is absent")

    if String.IsNullOrWhiteSpace(password) || password.Length < 32 then
        failtest "Old synthetic writer credential lacks a bounded private commitment"

    let bytes = Encoding.UTF8.GetBytes(password)

    try
        digest bytes
    finally
        CryptographicOperations.ZeroMemory(bytes)

let observed (access: RestoredPairAccess) =
    let requiredRole (source: string) =
        NpgsqlConnectionStringBuilder(source).Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Old synthetic writer role is absent")

    let primaryRole = requiredRole access.App
    let witnessRole = requiredRole access.WitnessWriter
    let primaryOid, primarySessions = roleFacts access.Owner primaryRole
    let witnessOid, witnessSessions = roleFacts access.WitnessOwner witnessRole
    let endpoint = Guid.NewGuid()
    let address = NpgsqlConnectionStringBuilder(access.App)

    // This identifies only the isolated in-process synthetic runtime, never an external Web route.
    let localAddress = $"synthetic-runtime|{endpoint:D}|{address.Host}|{address.Port}\n"

    {
        OldEndpointId = endpoint
        OldEndpointAddressSha256 = digest (Encoding.ASCII.GetBytes(localAddress))
        OldPrimaryRoleOid = primaryOid
        OldWitnessRoleOid = witnessOid
        OldPrimaryCredentialSha256 = credentialDigest access.App
        OldWitnessCredentialSha256 = credentialDigest access.WitnessWriter
        PrimarySessionSetSha256 = primarySessions
        WitnessSessionSetSha256 = witnessSessions
    }
