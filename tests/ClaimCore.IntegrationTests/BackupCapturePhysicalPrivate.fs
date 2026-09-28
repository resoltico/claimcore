module internal ClaimCore.IntegrationTests.BackupCapturePhysicalPrivate

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open Npgsql
open ClaimCore.IntegrationTests.FixtureEnvironment
open ClaimCore.IntegrationTests.RestorePhysicalProcess

[<NoEquality; NoComparison>]
type SigningKeyFiles =
    {
        PrivatePath: string
        PublicPath: string
        PublicBytes: byte array
    }

[<NoEquality; NoComparison>]
type CapturePaths =
    {
        Scratch: string
        Archive: string
        Checkpoint: string
        Inventory: string
        SignerLedger: string
        SocketRoot: string
        Socket: string
        ServiceFile: string
        ConfigFile: string
        SignerConfigFile: string
        AgeIdentity: string
    }

let private mode =
    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

let private createDirectory path =
    Directory.CreateDirectory(path, mode) |> ignore
    File.SetUnixFileMode(path, mode)

let roots () =
    let scratch = privateScratch ()
    let socketParent = if OperatingSystem.IsMacOS() then "/private/tmp" else "/tmp"

    let socketRoot =
        Path.Combine(socketParent, "cccp." + Guid.NewGuid().ToString("N").Substring(0, 12))

    createDirectory socketRoot
    let archive = Path.Combine(scratch, "archive")
    let checkpoint = Path.Combine(scratch, "checkpoint")
    let inventory = Path.Combine(scratch, "inventory")
    let ledger = Path.Combine(scratch, "signer-ledger")

    for path in [ archive; checkpoint; inventory; ledger ] do
        createDirectory path

    {
        Scratch = scratch
        Archive = archive
        Checkpoint = checkpoint
        Inventory = inventory
        SignerLedger = ledger
        SocketRoot = socketRoot
        Socket = Path.Combine(socketRoot, "s")
        ServiceFile = Path.Combine(scratch, "pg_service.conf")
        ConfigFile = Path.Combine(scratch, "capture-config.json")
        SignerConfigFile = Path.Combine(scratch, "checkpoint-signer.json")
        AgeIdentity = Path.Combine(scratch, "identity.age")
    }

let command executable arguments category =
    let status, output, _ = run executable arguments

    if status <> 0 then
        failtest category

    output

let keyPair (paths: CapturePaths) name =
    let privatePath = Path.Combine(paths.Scratch, name + ".key")
    let publicPath = Path.Combine(paths.Scratch, name + ".pub")
    let derPath = Path.Combine(paths.Scratch, name + ".der")

    command
        "openssl"
        [ "genpkey"; "-algorithm"; "Ed25519"; "-out"; privatePath ]
        "Ed25519 key generation failed"
    |> ignore

    command
        "openssl"
        [ "pkey"; "-in"; privatePath; "-pubout"; "-out"; publicPath ]
        "Ed25519 public key generation failed"
    |> ignore

    command
        "openssl"
        [ "pkey"; "-in"; privatePath; "-pubout"; "-outform"; "DER"; "-out"; derPath ]
        "Ed25519 public encoding failed"
    |> ignore

    File.SetUnixFileMode(privatePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    File.SetUnixFileMode(publicPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    let der = File.ReadAllBytes derPath
    let prefix = Convert.FromHexString("302a300506032b6570032100")

    if der.Length <> 44 || der[..11] <> prefix then
        failtest "Ed25519 public bytes are not raw32."

    {
        PrivatePath = privatePath
        PublicPath = publicPath
        PublicBytes = der[12..]
    }

let private safeService (value: string) =
    if
        String.IsNullOrWhiteSpace value
        || not (Regex.IsMatch(value, "^[A-Za-z0-9_.:-]{1,128}$"))
    then
        failtest "Synthetic libpq service component is unavailable."

    value

let private profile name (raw: string) includeDatabase =
    let source = NpgsqlConnectionStringBuilder raw
    let host = source.Host |> Option.ofObj |> Option.defaultValue "" |> safeService
    let user = source.Username |> Option.ofObj |> Option.defaultValue "" |> safeService

    let password =
        source.Password |> Option.ofObj |> Option.defaultValue "" |> safeService

    let database =
        source.Database |> Option.ofObj |> Option.defaultValue "" |> safeService

    let databaseLine = if includeDatabase then $"dbname={database}\n" else ""
    $"[{name}]\nhost={host}\nport={source.Port}\nuser={user}\npassword={password}\n{databaseLine}"

let writeServices (paths: CapturePaths) primary witness =
    let content =
        String.concat
            "\n"
            [
                profile "backup_primary" primary true
                profile "backup_primary_replication" primary false
                profile "backup_witness" witness true
                profile "backup_witness_replication" witness false
            ]

    privateFile paths.Scratch paths.ServiceFile content

let private replicationHba (container: string) (role: string) =
    if
        not (Regex.IsMatch(container, "^[0-9a-f]{64}$"))
        || not (Regex.IsMatch(role, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
    then
        failtest "Synthetic replication target is invalid."

    let rule =
        $"printf '\\nhost replication {role} all scram-sha-256\\n' >> /var/lib/postgresql/18/docker/pg_hba.conf"

    command
        "docker"
        [ "exec"; "-u"; "postgres"; container; "sh"; "-c"; rule ]
        "Synthetic replication HBA was unavailable"
    |> ignore

    command
        "docker"
        [
            "exec"
            "-u"
            "postgres"
            container
            "pg_ctl"
            "-D"
            "/var/lib/postgresql/18/docker"
            "reload"
        ]
        "Synthetic replication HBA reload failed"
    |> ignore

let prepareReplication (primary: string) (witness: string) =
    let primaryId, witnessId = ClaimCore.IntegrationTests.Fixtures.containerIds ()

    let primaryRole =
        (NpgsqlConnectionStringBuilder primary).Username
        |> Option.ofObj
        |> Option.defaultValue ""

    let witnessRole =
        (NpgsqlConnectionStringBuilder witness).Username
        |> Option.ofObj
        |> Option.defaultValue ""

    replicationHba primaryId primaryRole
    replicationHba witnessId witnessRole
