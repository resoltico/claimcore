module ClaimCore.IntegrationTests.CaseWitnessPruneFunctionTests

open System.Threading
open System
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ownerWitness (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private sql =
    "SELECT deleted_count FROM claimcore_witness.settle_and_prune("
    + "@installation,@lineage,@epoch,@event,@case,@intentSequence,@intentHash,"
    + "@purge,@purgeSequence,@purgeHash,@cutoff,@cutoffHash,@targetCount,@targetDigest,"
    + "@approvalOne,@approvalOneSequence,@approvalOneHash,"
    + "@approvalTwo,@approvalTwoSequence,@approvalTwoHash,@key,@settlement,@writerCapability)"

let private receipt (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()
    use transaction = connection.BeginTransaction()

    CaseTombstonePrunePrimaryRead.find connection transaction fixture.CaseId
    |> await
    |> Option.defaultWith (fun () -> failtest "Committed prune receipt is missing")

let private approvalTickets (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT approval_id,witness_sequence,witness_entry_hash "
            + "FROM claimcore.case_erasure_prune_approvals WHERE case_id=@case "
            + "AND prune_event_id=@event ORDER BY approver_actor_id",
            connection
        )

    Sql.uuid command "case" fixture.CaseId
    Sql.uuid command "event" fixture.Action.EventId
    use reader = command.ExecuteReader()
    let values = ResizeArray<Guid * int64 * byte array>()

    while reader.Read() do
        values.Add(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<byte array>(2))

    if values.Count <> 2 then
        failtest "Exactly two witnessed prune approvals are required"

    values[0], values[1]

let private bindIdentity
    (command: NpgsqlCommand)
    (fixture: PruneFixture)
    (stored: StoredWitnessPruneReceipt)
    =
    Sql.uuid command "installation" fixture.Witness.Identity.InstallationId
    Sql.uuid command "lineage" fixture.Witness.Identity.LineageId
    Sql.integer command "epoch" fixture.Witness.Identity.Epoch
    Sql.uuid command "event" fixture.Action.EventId
    Sql.uuid command "case" fixture.CaseId
    Sql.integer command "intentSequence" stored.IntentSequence
    Sql.add command "intentHash" NpgsqlDbType.Bytea (box stored.IntentHash)
    Sql.uuid command "purge" stored.PurgeEventId
    Sql.integer command "purgeSequence" stored.PurgeWitnessSequence
    Sql.add command "purgeHash" NpgsqlDbType.Bytea (box stored.PurgeWitnessHash)

let private bindProof
    (command: NpgsqlCommand)
    (fixture: PruneFixture)
    (stored: StoredWitnessPruneReceipt)
    (firstId, firstSeq, firstHash)
    (secondId, secondSeq, secondHash)
    =
    Sql.integer command "cutoff" stored.CutoffSequence
    Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box stored.CutoffHash)
    Sql.integer command "targetCount" stored.TargetCount

    let targetCount, witnessDigest =
        CaseWitnessPayloadTargets.witnessDigest
            fixture.Witness
            fixture.CaseId
            stored.CutoffSequence
            stored.CutoffHash
            CancellationToken.None
        |> await

    Expect.equal targetCount stored.TargetCount "Witness and primary target counts agree"
    Sql.add command "targetDigest" NpgsqlDbType.Bytea (box witnessDigest)
    Sql.uuid command "approvalOne" firstId
    Sql.integer command "approvalOneSequence" firstSeq
    Sql.add command "approvalOneHash" NpgsqlDbType.Bytea (box firstHash)
    Sql.uuid command "approvalTwo" secondId
    Sql.integer command "approvalTwoSequence" secondSeq
    Sql.add command "approvalTwoHash" NpgsqlDbType.Bytea (box secondHash)

let private call
    (connection: NpgsqlConnection)
    (fixture: PruneFixture)
    (stored: StoredWitnessPruneReceipt)
    approvals
    (settlement: Evidence)
    (capability: byte array)
    mutate
    =
    use command = new NpgsqlCommand(sql, connection)
    bindIdentity command fixture stored
    let first, second = approvals
    bindProof command fixture stored first second
    Sql.uuid command "key" settlement.Ticket.KeyId
    Sql.add command "settlement" NpgsqlDbType.Bytea (box settlement.EncryptedPayload)
    Sql.add command "writerCapability" NpgsqlDbType.Bytea (box capability)
    mutate command
    command.ExecuteScalar()

let private run owner source witness runtime proposer first second _ writer =
    let fixture =
        prepare (fun _ _ -> ()) owner source witness runtime proposer first second writer

    assertFirstPrune fixture
    fullAudit fixture
    let stored = receipt fixture
    let approvals = approvalTickets fixture

    let settled =
        (witness.EvidenceStore
            .TryReadEvidence(fixture.Action.EventId, SettledAuthority, CancellationToken.None)
            .GetAwaiter()
            .GetResult())
        |> Option.defaultWith (fun () -> failtest "Settled prune evidence is missing")

    let path =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Isolated writer capability file is absent")

    use privateFile = WriterCapabilityFile.Load(path)
    use connection = new NpgsqlConnection(ownerWitness writer)
    connection.Open()

    privateFile.Use(fun capability ->
        Expect.equal
            (call connection fixture stored approvals settled capability ignore :?> int64)
            0L
            "Valid owner replay is idempotent before invalid-parameter controls"

        for name in
            [
                "installation"
                "lineage"
                "event"
                "case"
                "purge"
                "approvalOne"
                "approvalTwo"
                "key"
            ] do
            for changed in [ box DBNull.Value; box Guid.Empty ] do
                Expect.throwsT<PostgresException>
                    (fun () ->
                        call
                            connection
                            fixture
                            stored
                            approvals
                            settled
                            capability
                            (fun command -> command.Parameters[name].Value <- changed)
                        |> ignore)
                    "Owner SQL rejects NULL or zero identity before replay")

    fullAudit fixture

let tests =
    testList
        "witness prune owner SQL"
        [
            testCase
                "[CC-ERASE-001] owner prune function rejects NULL and zero UUID identities"
                (fun _ -> CaseLifecycleStoreFixture.setup run)
        ]
