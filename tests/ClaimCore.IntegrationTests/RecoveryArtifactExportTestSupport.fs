module internal ClaimCore.IntegrationTests.RecoveryArtifactExportTestSupport

open System
open System.IO
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

[<NoEquality; NoComparison>]
type internal Scenario =
    {
        OwnerConnection: string
        Source: NpgsqlDataSource
        Witness: WitnessProtocol
        Retained: RetainedPreparation
        Context: ActorCallContext
        Now: DateTimeOffset
        Encryption: byte array
        Mac: byte array
        Key: RecoveryArtifactIssueKey
    }

let acceptedCase (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
    | _ -> failtest "Synthetic case must accept before recovery export."

let grant source witness owner role =
    let store = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        owner,
        actorId store owner,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let inventoryCount owner exportId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM claimcore.recovery_artifact_exports WHERE export_id=@export),"
            + "(SELECT count(*) FROM claimcore.managed_copies WHERE copy_id=@export)",
            connection
        )

    Sql.uuid command "export" exportId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic export inventory counts must exist"
    reader.GetInt64(0), reader.GetInt64(1)

let ageAccepted owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET recorded_at=clock_timestamp()-interval '3 days' "
            + "WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Expect.equal (command.ExecuteNonQuery()) 1 "Only synthetic receipt is aged"

let preparationCount owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.request_preparations WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId
    command.ExecuteScalar() :?> int64

let tamperAndRestore owner exportId (original: byte array) (action: unit -> unit) =
    let changed = Array.copy original
    changed[0] <- changed[0] ^^^ 1uy
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let update bytes =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.recovery_artifact_payloads SET artifact_bytes=@bytes "
                + "WHERE export_id=@export",
                connection
            )

        Sql.uuid command "export" exportId
        Sql.add command "bytes" NpgsqlTypes.NpgsqlDbType.Bytea (box bytes)
        Expect.equal (command.ExecuteNonQuery()) 1 "Only synthetic export is changed"

    update changed

    try
        action ()
    finally
        update original

let forgeAndClearVerificationProof owner exportId (action: unit -> unit) =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let update proof =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.managed_copies SET verification_proof_sha256=@proof "
                + "WHERE copy_id=@export",
                connection
            )

        Sql.uuid command "export" exportId
        Sql.optional command "proof" NpgsqlTypes.NpgsqlDbType.Bytea proof
        Expect.equal (command.ExecuteNonQuery()) 1 "Only synthetic export custody is changed"

    update (Some(Array.create 32 0xA5uy))

    try
        action ()
    finally
        update None

let runtimeCannotPrune source operationId =
    use connection = RuntimeDatabase.openConnection source

    use command =
        new NpgsqlCommand(
            "DELETE FROM claimcore.request_preparations WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId

    try
        command.ExecuteNonQuery() |> ignore
        failtest "Runtime role must not delete a retained preparation."
    with :? PostgresException as error ->
        Expect.equal error.SqlState "42501" "Pruning remains owner-only"

let removeAndRestorePayload
    owner
    exportId
    (artifactBytes: byte array)
    (canonicalAction: byte array)
    (action: unit -> unit)
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use delete =
        new NpgsqlCommand(
            "DELETE FROM claimcore.recovery_artifact_payloads WHERE export_id=@export",
            connection
        )

    Sql.uuid delete "export" exportId
    Expect.equal (delete.ExecuteNonQuery()) 1 "Only synthetic encrypted payload is removed"

    try
        action ()
    finally
        use restore =
            new NpgsqlCommand(
                "INSERT INTO claimcore.recovery_artifact_payloads "
                + "(export_id,artifact_bytes,canonical_action) VALUES (@export,@bytes,@canonical)",
                connection
            )

        Sql.uuid restore "export" exportId
        Sql.add restore "bytes" NpgsqlTypes.NpgsqlDbType.Bytea (box artifactBytes)
        Sql.add restore "canonical" NpgsqlTypes.NpgsqlDbType.Bytea (box canonicalAction)
        Expect.equal (restore.ExecuteNonQuery()) 1 "Synthetic encrypted payload is restored"

let checkAudit (scenario: Scenario) id (row: RecoveryArtifactExportRow) =
    use connection = RuntimeDatabase.openConnection scenario.Source

    DataAudit.run connection scenario.Witness CancellationToken.None
    |> await
    |> ignore

    tamperAndRestore scenario.OwnerConnection id row.ArtifactBytes (fun () ->
        Expect.throwsT<InvalidDataException>
            (fun () ->
                DataAudit.run connection scenario.Witness CancellationToken.None
                |> await
                |> ignore)
            "Full audit refuses owner-changed encrypted export")

    forgeAndClearVerificationProof scenario.OwnerConnection id (fun () ->
        Expect.throwsT<InvalidDataException>
            (fun () ->
                DataAudit.run connection scenario.Witness CancellationToken.None
                |> await
                |> ignore)
            "An unknown export cannot acquire verification proof by projection change")

    removeAndRestorePayload
        scenario.OwnerConnection
        id
        row.ArtifactBytes
        row.CanonicalAction
        (fun () ->
            Expect.equal
                (inventoryCount scenario.OwnerConnection id)
                (1L, 1L)
                "Receipt and copy remain"

            Expect.throwsT<InvalidDataException>
                (fun () ->
                    DataAudit.run connection scenario.Witness CancellationToken.None
                    |> await
                    |> ignore)
                "Active export without encrypted payload fails full audit")

    DataAudit.run connection scenario.Witness CancellationToken.None
    |> await
    |> ignore
