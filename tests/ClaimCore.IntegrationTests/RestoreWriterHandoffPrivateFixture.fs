module internal ClaimCore.IntegrationTests.RestoreWriterHandoffPrivateFixture

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestoreWriterHandoffFencePins

let private fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (values: SortedDictionary<string, objnull>) name value = values.Add(name, box value)

let private canonical (values: SortedDictionary<string, objnull>) =
    Array.append (JsonSerializer.SerializeToUtf8Bytes(values)) [| byte '\n' |]

let private stamp (value: DateTimeOffset) =
    value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

let private digest (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let fenceBody
    (report: RestoreReportClaims)
    (oldGeneration: int64)
    (newGeneration: int64)
    (signerKeyId: Guid)
    (independentProbeSha: string)
    (pins: WriterFencePins)
    (reportSha: string)
    (checkedAt: DateTimeOffset)
    (validUntil: DateTimeOffset)
    =
    let values = fields ()
    put values "format" "claimcore-old-writer-isolation-1"
    put values "installationId" (report.InstallationId.ToString("D"))
    put values "lineageId" (report.LineageId.ToString("D"))
    put values "epoch" report.Epoch
    put values "oldGeneration" oldGeneration
    put values "newGeneration" newGeneration
    put values "reportSha256" reportSha
    put values "independentProbeSha256" independentProbeSha
    put values "oldEndpointId" (pins.OldEndpointId.ToString("D"))
    put values "oldEndpointAddressSha256" pins.OldEndpointAddressSha256
    put values "oldPrimaryRoleOid" pins.OldPrimaryRoleOid
    put values "oldWitnessRoleOid" pins.OldWitnessRoleOid
    put values "oldPrimaryCredentialSha256" pins.OldPrimaryCredentialSha256
    put values "oldWitnessCredentialSha256" pins.OldWitnessCredentialSha256
    put values "primarySessionSetSha256" pins.PrimarySessionSetSha256
    put values "witnessSessionSetSha256" pins.WitnessSessionSetSha256
    put values "checkpointSignerKeyId" (signerKeyId.ToString("D"))

    for name in
        [
            "oldWriterStopped"
            "primarySessionsTerminated"
            "witnessSessionsTerminated"
            "oldEndpointIsolated"
            "primaryCredentialRevoked"
            "witnessCredentialRevoked"
            "preIsolationCommitReconciled"
        ] do
        put values name true

    put values "checkedAt" (stamp checkedAt)
    put values "validUntil" (stamp validUntil)
    canonical values

let private write root name maximum (bytes: byte array) =
    let path = Path.Combine(root, name)

    match PrivateFileService.writeNew maximum path bytes with
    | Ok() -> path
    | Error _ -> failtest "Synthetic owner-private W1 file could not be created"

let createRoot scratch =
    let root = Path.Combine(scratch, "writer-handoff")

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(root, mode) |> ignore
    root

let signedFile root name bytes signature =
    write root (name + ".json") 131072 bytes, write root (name + ".sig") 64 signature

let rawCapability root bytes =
    write root "new-writer-capability.key" 32 bytes

let private connection root name value =
    write root (name + ".connection") 8192 (Encoding.UTF8.GetBytes(value + "\n"))

let withOwnerEnvironment
    root
    (access: RestoredPairAccess)
    archiveRoot
    reportPath
    reportSignaturePath
    indexPath
    fencePath
    fenceSignaturePath
    newCapabilityPath
    action
    =
    let settings =
        [
            "CLAIMCORE_CONNECTION_FILE", connection root "app" access.App
            "CLAIMCORE_WITNESS_CONNECTION_FILE",
            connection root "witness-writer" access.WitnessWriter
            "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE",
            connection root "witness-audit" access.WitnessAudit
            "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE",
            connection root "witness-owner" access.WitnessOwner
            "CLAIMCORE_WRITER_RESTORE_REPORT_FILE", reportPath
            "CLAIMCORE_WRITER_RESTORE_SIGNATURE_FILE", reportSignaturePath
            "CLAIMCORE_WRITER_RESTORE_INDEX_FILE", indexPath
            "CLAIMCORE_WRITER_FENCE_FILE", fencePath
            "CLAIMCORE_WRITER_FENCE_SIGNATURE_FILE", fenceSignaturePath
            "CLAIMCORE_NEW_WRITER_CAPABILITY_FILE", newCapabilityPath
            "CLAIMCORE_RESTORE_ARCHIVE_ROOT", archiveRoot
        ]

    let prior =
        settings
        |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

    try
        for name, value in settings do
            Environment.SetEnvironmentVariable(name, value)

        action ()
    finally
        for name, value in prior do
            Environment.SetEnvironmentVariable(name, value)

let reportFiles root (produced: SignedRestoreProduction) =
    let report, signature =
        signedFile root "restore-report" produced.Evidence.Report produced.ReportSignature

    let index = write root "restore-index.json" 131072 produced.Evidence.EvidenceIndex
    report, signature, index

let fenceDigest bytes = digest bytes

let waitForFenceObservation (instant: DateTimeOffset) =
    let remaining = instant - DateTimeOffset.UtcNow

    if remaining > TimeSpan.Zero then
        Thread.Sleep(remaining)

let rotateWriterCredentials (access: RestoredPairAccess) =
    let rotate (connectionString: string) (role: string) =
        let password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
        use owner = new NpgsqlConnection(connectionString)
        owner.Open()

        use change =
            new NpgsqlCommand("ALTER ROLE " + role + " PASSWORD '" + password + "'", owner)

        change.ExecuteNonQuery() |> ignore

        use terminate =
            new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename='"
                + role
                + "' AND pid <> pg_backend_pid()",
                owner
            )

        use closed = terminate.ExecuteReader()

        while closed.Read() do
            closed.GetBoolean(0) |> ignore

        closed.Close()

        use sessions =
            new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE usename='"
                + role
                + "' AND pid <> pg_backend_pid()",
                owner
            )

        let mutable remaining = 0L

        for _ in 1..10 do
            remaining <- sessions.ExecuteScalar() :?> int64

            if remaining > 0L then
                Thread.Sleep(100)

        Expect.equal remaining 0L "Stopped old writer has no retained database session"

        password

    let primaryPassword = rotate access.Owner "claimcore_app"
    let witnessPassword = rotate access.WitnessOwner "claimcore_witness_writer"

    let changed (source: string) (password: string) =
        let value = NpgsqlConnectionStringBuilder(source)
        value.Password <- password
        value.ConnectionString

    let denied (source: string) =
        let value = NpgsqlConnectionStringBuilder(source)
        value.Pooling <- false

        Expect.throws
            (fun () ->
                use old = new NpgsqlConnection(value.ConnectionString)
                old.Open())
            "Old writer credential cannot reopen the restored pair"

    denied access.App
    denied access.WitnessWriter

    { access with
        App = changed access.App primaryPassword
        WitnessWriter = changed access.WitnessWriter witnessPassword
    }
