module ClaimCore.IntegrationTests.CaseTombstoneTerminalAuditTamperTests

open System
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private withApproved action =
    withPruned (fun fixture source runtime _ first second _ ->
        let draft = TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)
        let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)
        let approvalId = Guid.NewGuid()
        approve runtime first draft approvalId expiry |> applied approvalId
        fullAudit fixture
        action fixture source second approvalId)

let private primaryMutation statement parameters =
    withApproved (fun fixture source second approvalId ->
        use connection = new NpgsqlConnection(fixture.Owner)
        connection.Open()
        use command = new NpgsqlCommand(statement, connection)
        Sql.uuid command "approval" approvalId
        parameters source second command
        Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic approval was tampered"

        Expect.throws
            (fun () -> fullAudit fixture)
            "Terminal approval tamper must quarantine full data audit")

let private actorTamper =
    testCase "[CC-ERASE-001] terminal approval actor tamper quarantines audit" (fun _ ->
        primaryMutation
            ("UPDATE claimcore.case_erasure_terminal_approvals "
             + "SET approver_actor_id=@actor WHERE approval_id=@approval")
            (fun source second command ->
                let actor = actorId (fst source) second
                Sql.uuid command "actor" actor))

let private grantTamper =
    testCase "[CC-ERASE-001] terminal approval grant revision tamper quarantines audit" (fun _ ->
        primaryMutation
            ("UPDATE claimcore.case_erasure_terminal_approvals "
             + "SET approver_grant_revision=approver_grant_revision+1 WHERE approval_id=@approval")
            (fun _ _ _ -> ()))

let private canonicalTamper =
    testCase "[CC-ERASE-001] terminal approval canonical tamper quarantines audit" (fun _ ->
        primaryMutation
            ("UPDATE claimcore.case_erasure_terminal_approvals "
             + "SET canonical_action=decode('7b7d','hex') WHERE approval_id=@approval")
            (fun _ _ _ -> ()))

let private witnessOwner (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private witnessScopeTamper =
    testCase "[CC-ERASE-001] terminal approval witness scope tamper quarantines audit" (fun _ ->
        withApproved (fun fixture _ _ approvalId ->
            use connection = new NpgsqlConnection(witnessOwner fixture.Writer)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore_witness.journal SET scope_kind='INSTALLATION',"
                    + "subject_case_id=NULL WHERE operation_id=@approval AND phase='INTENT'",
                    connection
                )

            Sql.uuid command "approval" approvalId
            Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic witness scope was changed"

            Expect.throws
                (fun () -> fullAudit fixture)
                "Wrong-scope witness approval must quarantine audit"))

let tests =
    testList
        "terminal approval audit tamper"
        [ actorTamper; grantTamper; canonicalTamper; witnessScopeTamper ]
