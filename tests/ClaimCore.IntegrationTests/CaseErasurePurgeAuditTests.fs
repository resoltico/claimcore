module ClaimCore.IntegrationTests.CaseErasurePurgeAuditTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseErasurePurgeTests

let private ct = CancellationToken.None

let private purged action =
    CaseLifecycleStoreFixture.setup (fun owner _ witness runtime proposer first second _ writer ->
        let actor, input, change = proposal runtime proposer first second
        let id = caseId owner input.CaseReference
        let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
        let draft = CaseLifecycleCandidate.draft id change
        use connection = new NpgsqlConnection(owner)
        connection.Open()

        match
            CaseErasurePurge.execute
                owner
                connection
                witness
                commitments
                (syntheticInventory id)
                draft
                ct
            |> await
        with
        | OwnerPurgeOutcome.Purged _ ->
            action owner writer witness commitments id actor input change
        | _ -> failtest "Synthetic purge prerequisite failed.")

let private auditRefuses owner witness commitments =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression connection witness (Some commitments) ct
            |> await
            |> ignore)
        "A changed purged-state proof must quarantine full audit"

let private auditDiverges owner witness commitments =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    Expect.throwsT<InvalidDataException>
        (fun () ->
            DataAudit.runWithSuppression connection witness (Some commitments) ct
            |> await
            |> ignore)
        "A changed retained approval is an evidence divergence, not an unexpected audit fault"

let private changedDenialSeal =
    testCase "[CC-AUDIT-001] altered keyed denial root quarantines purged audit" (fun _ ->
        purged (fun owner _ witness commitments id _ _ _ ->
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use tamper =
                new NpgsqlCommand(
                    "UPDATE claimcore.case_erasure_tombstones "
                    + "SET purge_denial_set_sha256=decode(repeat('ff',32),'hex') "
                    + "WHERE case_id=@case",
                    connection
                )

            Sql.uuid tamper "case" id
            Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic root was changed"
            auditRefuses owner witness commitments))

let private missingApproval =
    testCase "[CC-AUDIT-001] missing retained steward proof quarantines purged audit" (fun _ ->
        purged (fun owner _ witness commitments id _ _ _ ->
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use remove =
                new NpgsqlCommand(
                    "DELETE FROM claimcore.case_erasure_purge_approvals "
                    + "WHERE ctid IN (SELECT ctid FROM claimcore.case_erasure_purge_approvals "
                    + "WHERE case_id=@case ORDER BY approval_id LIMIT 1)",
                    connection
                )

            Sql.uuid remove "case" id
            Expect.equal (remove.ExecuteNonQuery()) 1 "One synthetic approval was removed"
            auditDiverges owner witness commitments))

let private restoreSyntheticCase owner id reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use insert =
        new NpgsqlCommand(
            "INSERT INTO claimcore.cases "
            + "(case_id,incident_date,incident_notification_date,incident_country,"
            + "claimant_name,insurer_name,claimed_amount,claimed_currency,case_reference,"
            + "status,revision) VALUES (@case,DATE '2026-01-01',DATE '2026-01-02',"
            + "'LV','Synthetic Claimant','Synthetic Insurer',10,'EUR',@reference,'OPENED',1)",
            connection
        )

    Sql.uuid insert "case" id
    Sql.text insert "reference" reference
    Expect.equal (insert.ExecuteNonQuery()) 1 "One stale synthetic case was restored"

let private retainedLiveRow =
    testCase "[CC-AUDIT-001] restored claimant row beside purge proof quarantines audit" (fun _ ->
        purged (fun owner _ witness commitments id _ input _ ->
            restoreSyntheticCase owner id input.CaseReference
            auditRefuses owner witness commitments))

let private lostPurgeFence =
    testCase "[CC-AUDIT-001] restored primary missing purge tombstone quarantines audit" (fun _ ->
        purged (fun owner _ witness commitments id _ input _ ->
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            for table in
                [
                    "case_erasure_purge_approvals"
                    "case_erasure_operation_denials"
                    "case_erasure_authority_tip"
                ] do
                use command =
                    new NpgsqlCommand(
                        $"DELETE FROM claimcore.{table} WHERE case_id=@case",
                        connection
                    )

                Sql.uuid command "case" id
                command.ExecuteNonQuery() |> ignore

            use fence =
                new NpgsqlCommand(
                    "DELETE FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
                    connection
                )

            Sql.uuid fence "case" id
            Expect.equal (fence.ExecuteNonQuery()) 1 "Synthetic fence was removed"
            restoreSyntheticCase owner id input.CaseReference
            auditRefuses owner witness commitments))

let private witnessOwner (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private missingWitnessEntry suffix =
    testCase suffix (fun _ ->
        purged (fun owner writer witness commitments id _ _ _ ->
            use primary = new NpgsqlConnection(owner)
            primary.Open()

            let sequenceSql =
                if suffix.Contains("middle", StringComparison.Ordinal) then
                    "SELECT request_witness_sequence FROM claimcore.case_erasure_tombstones WHERE case_id=@case"
                else
                    "SELECT purge_witness_sequence FROM claimcore.case_erasure_tombstones WHERE case_id=@case"

            use sequence = new NpgsqlCommand(sequenceSql, primary)

            Sql.uuid sequence "case" id
            let target = sequence.ExecuteScalar() :?> int64
            use independent = new NpgsqlConnection(witnessOwner writer)
            independent.Open()

            for table in [ "journal_payloads"; "journal" ] do
                use remove =
                    new NpgsqlCommand(
                        $"DELETE FROM claimcore_witness.{table} WHERE sequence=@sequence",
                        independent
                    )

                Sql.integer remove "sequence" target
                Expect.equal (remove.ExecuteNonQuery()) 1 "One synthetic witness row was removed"

            auditRefuses owner witness commitments))

let tests =
    testList
        "case erasure purged-state audit"
        [
            changedDenialSeal
            missingApproval
            retainedLiveRow
            lostPurgeFence
            missingWitnessEntry "[CC-AUDIT-001] missing middle CASE intent quarantines purged audit"
            missingWitnessEntry "[CC-AUDIT-001] missing suffix CASE purge quarantines audit"
        ]
