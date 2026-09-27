module internal ClaimCore.IntegrationTests.BackupCaptureEvidenceProcess

open System
open System.IO
open Expecto
open Npgsql
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixtureEnvironment

let privateRoots () =
    let temporary = Path.GetTempPath()

    let canonical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let parent =
        Path.Combine(canonical, "claimcore-capture-evidence-" + Guid.NewGuid().ToString("N"))

    let archive = Path.Combine(parent, "archive")
    let checkpoint = Path.Combine(parent, "checkpoint")

    for directory in [ parent; archive; checkpoint ] do
        Directory.CreateDirectory(directory) |> ignore

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

    parent, archive, checkpoint

let witnessRoleConnection (source: string) (writer: string) =
    let selected = NpgsqlConnectionStringBuilder(writer)
    let role = NpgsqlConnectionStringBuilder(source)
    role.Host <- selected.Host
    role.Port <- selected.Port
    role.Database <- selected.Database
    role.ConnectionString

let processInputs (directory: string) owner app writer witness archive checkpoint =
    let appPath = Path.Combine(directory, "application.connection")
    let witnessOwner = Path.Combine(directory, "witness-owner.connection")
    let witnessAudit = Path.Combine(directory, "witness-audit.connection")
    privateFile directory appPath app
    privateFile directory witnessOwner (witnessRoleConnection (witnessOwnerConnection ()) writer)
    privateFile directory witnessAudit (witnessRoleConnection (witnessAuditConnection ()) writer)

    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Active synthetic writer capability is missing.")

    ClaimCore.IntegrationTests.DatabaseVerifyDataTests.files directory owner writer witness
    @ [
        "CLAIMCORE_CONNECTION_FILE", appPath
        "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE", witnessOwner
        "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE", witnessAudit
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
        "CLAIMCORE_BACKUP_ARCHIVE_ROOT", archive
        "CLAIMCORE_BACKUP_CHECKPOINT_ROOT", checkpoint
    ]

let processStatus configured (leaseId: Guid) =
    let code, result =
        ClaimCore.IntegrationTests.DatabaseVerifyDataTests.runCommand
            "reconcile-backup-capture"
            [ leaseId.ToString("D") ]
            configured

    use response = result

    let status =
        response.RootElement.GetProperty("operationOutcome").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Owner capture readback status is missing.")

    code, status
