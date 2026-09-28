module ClaimCore.IntegrationTests.CaseWitnessPayloadPruneAuditTests

open System
open System.Threading
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ct = CancellationToken.None

let private ownerWitness (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private withPruned action =
    CaseLifecycleStoreFixture.setup
        (fun owner source witness runtime proposer first second _ writer ->
            let fixture =
                prepare (fun _ _ -> ()) owner source witness runtime proposer first second writer

            assertFirstPrune fixture
            assertPrunedMetadata fixture
            fullAudit fixture
            action fixture)

let private auditRefuses (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression connection fixture.Witness (Some fixture.Commitments) ct
            |> await
            |> ignore)
        "Postprune full audit must quarantine changed evidence"

let private primaryMutation (fixture: PruneFixture) statement =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()
    use command = new NpgsqlCommand(statement, connection)
    Sql.uuid command "case" fixture.CaseId
    Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic primary proof was changed"
    auditRefuses fixture

let private missingTarget =
    testCase "[CC-ERASE-001] missing postprune target row quarantines full audit" (fun _ ->
        withPruned (fun fixture ->
            primaryMutation
                fixture
                ("DELETE FROM claimcore.case_erasure_prune_targets WHERE (case_id,sequence) IN "
                 + "(SELECT case_id,sequence FROM claimcore.case_erasure_prune_targets "
                 + "WHERE case_id=@case ORDER BY sequence LIMIT 1)")))

let private changedTargetRoot =
    testCase "[CC-ERASE-001] changed postprune target root quarantines full audit" (fun _ ->
        withPruned (fun fixture ->
            primaryMutation
                fixture
                ("UPDATE claimcore.case_erasure_tombstones SET "
                 + "witness_prune_target_digest=decode(repeat('fe',32),'hex') WHERE case_id=@case")))

let private missingApproval =
    testCase "[CC-ERASE-001] missing prune approval receipt quarantines full audit" (fun _ ->
        withPruned (fun fixture ->
            primaryMutation
                fixture
                ("DELETE FROM claimcore.case_erasure_prune_approvals WHERE ctid IN "
                 + "(SELECT ctid FROM claimcore.case_erasure_prune_approvals "
                 + "WHERE case_id=@case ORDER BY approval_id LIMIT 1)")))

let private witnessMutation (fixture: PruneFixture) statement =
    use connection = new NpgsqlConnection(ownerWitness fixture.Writer)
    connection.Open()
    use command = new NpgsqlCommand(statement, connection)
    Sql.uuid command "case" fixture.CaseId
    Sql.uuid command "event" fixture.Action.EventId
    Sql.integer command "cutoff" fixture.Action.CutoffSequence
    Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic witness proof was changed"
    auditRefuses fixture

let private missingWitnessMetadata =
    testCase "[CC-ERASE-001] missing middle CASE witness metadata quarantines full audit" (fun _ ->
        withPruned (fun fixture ->
            witnessMutation
                fixture
                ("DELETE FROM claimcore_witness.journal WHERE (installation_id,sequence) IN "
                 + "(SELECT installation_id,sequence FROM claimcore_witness.journal "
                 + "WHERE subject_case_id=@case AND sequence<=@cutoff "
                 + "ORDER BY sequence OFFSET 1 LIMIT 1)")))

let private missingPruneSettlementPayload =
    testCase
        "[CC-ERASE-001] missing retained prune settlement ciphertext quarantines full audit"
        (fun _ ->
            withPruned (fun fixture ->
                witnessMutation
                    fixture
                    ("DELETE FROM claimcore_witness.journal_payloads WHERE (installation_id,sequence) IN "
                     + "(SELECT installation_id,sequence FROM claimcore_witness.journal "
                     + "WHERE operation_id=@event AND phase='SETTLED_AUTHORITY')")))

let private resurrectedCiphertext =
    testCase "[CC-ERASE-001] reintroduced CASE ciphertext quarantines full audit" (fun _ ->
        withPruned (fun fixture ->
            use connection = new NpgsqlConnection(ownerWitness fixture.Writer)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore_witness.journal_payloads "
                    + "(installation_id,sequence,subject_case_id,encrypted_payload) "
                    + "SELECT installation_id,sequence,subject_case_id,@payload "
                    + "FROM claimcore_witness.journal WHERE subject_case_id=@case "
                    + "AND sequence<=@cutoff ORDER BY sequence LIMIT 1",
                    connection
                )

            Sql.uuid command "case" fixture.CaseId
            Sql.integer command "cutoff" fixture.Action.CutoffSequence

            command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, [| 1uy; 2uy; 3uy |])
            |> ignore

            Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic ciphertext reappeared"
            auditRefuses fixture))

let tests =
    testList
        "postprune evidence audit"
        [
            missingTarget
            changedTargetRoot
            missingApproval
            missingWitnessMetadata
            missingPruneSettlementPayload
            resurrectedCiphertext
        ]
