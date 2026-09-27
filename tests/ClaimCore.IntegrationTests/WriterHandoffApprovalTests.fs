module ClaimCore.IntegrationTests.WriterHandoffApprovalTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests

let internal request keyId (tip: Snapshot) =
    {
        ApprovalId = Guid.NewGuid()
        HandoffId = Guid.NewGuid()
        OldGeneration = tip.WriterGeneration
        ExpectedWitnessSequence = tip.TipSequence
        ExpectedWitnessHash = Array.copy tip.TipHash
        NewCapabilitySha256 = SHA256.HashData(Array.create 32 0x71uy)
        CheckpointSigningKeyId = keyId
        FenceReportSha256 = SHA256.HashData(Array.create 32 0x72uy)
        InventorySha256 = SHA256.HashData(Array.create 32 0x73uy)
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15.)
    }

let private denyUntrusted
    (runtime: Runtime)
    (witness: WitnessProtocol)
    holder
    (action: WriterHandoffApprovalRequest)
    =
    let approve actor value =
        (runtime.ForActor actor).ApproveWriterHandoff(value, CancellationToken.None)
        |> await

    let before = witness.Snapshot().TipSequence

    Expect.equal
        (approve holder action)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "A checkpoint holder cannot approve as an installation owner."

    Expect.equal
        (approve (service "handoff-service") action)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "Service identity cannot approve a handoff."

    Expect.equal (witness.Snapshot().TipSequence) before "Definite denials append no witness row."

let private denyInvalid
    (runtime: Runtime)
    (witness: WitnessProtocol)
    principal
    (action: WriterHandoffApprovalRequest)
    =
    let approve value =
        (runtime.ForActor principal).ApproveWriterHandoff(value, CancellationToken.None)
        |> await

    let before = witness.Snapshot().TipSequence

    let invalid =
        [
            { action with
                ApprovalId = Guid.NewGuid()
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1.)
            }
            { action with
                ApprovalId = Guid.NewGuid()
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2.)
            }
            { action with
                ApprovalId = Guid.NewGuid()
                ExpectedWitnessHash = Array.create 32 0xEEuy
            }
        ]

    for request in invalid do
        Expect.equal
            (approve request)
            WriterHandoffApprovalOutcome.ResourceUnavailable
            "Expired, distant or stale-tip approval is refused."

    Expect.equal
        (witness.Snapshot().TipSequence)
        before
        "Invalid approvals create no witness intent."

let private approveAndRetry
    (runtime: Runtime)
    (witness: WitnessProtocol)
    principal
    holder
    (action: WriterHandoffApprovalRequest)
    =
    let approve actor value =
        (runtime.ForActor actor).ApproveWriterHandoff(value, CancellationToken.None)
        |> await

    let approvedRevision =
        match approve principal action with
        | WriterHandoffApprovalOutcome.Approved(id, revision) when
            id = action.ApprovalId && revision > 0L
            ->
            revision
        | _ -> failtest "Human owner handoff approval was not witnessed."

    let grantEvent = Guid.NewGuid()

    (runtime.ForActor principal)
        .Management.SetGrant(
            grantEvent,
            holder,
            Role.CaseReader,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement grantEvent

    let after = witness.Snapshot().TipSequence

    match approve principal action with
    | WriterHandoffApprovalOutcome.Approved(id, revision) when
        id = action.ApprovalId && revision = approvedRevision
        ->
        ()
    | _ -> failtest "Exact owner handoff approval retry diverged."

    Expect.equal (witness.Snapshot().TipSequence) after "Exact retry appends no second intent."

    let changed =
        { action with
            InventorySha256 = Array.create 32 0xFFuy
        }

    Expect.equal
        (approve principal changed)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "An existing approval ID cannot authorize changed inventory bytes."

    Expect.equal (witness.Snapshot().TipSequence) after "Changed-byte replay appends no event."

let private checkpointHolderEnabled connectionString keyId =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use status =
        new NpgsqlCommand(
            "SELECT a.enabled FROM claimcore.managed_copy_signers s "
            + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
            + "WHERE s.signing_key_id=@key",
            connection
        )

    Sql.uuid status "key" keyId
    status.ExecuteScalar() :?> bool

let private disableAndDeny
    (runtime: Runtime)
    (witness: WitnessProtocol)
    owner
    app
    principal
    holder
    (action: WriterHandoffApprovalRequest)
    =
    let approve actor value =
        (runtime.ForActor actor).ApproveWriterHandoff(value, CancellationToken.None)
        |> await

    let disableEvent = Guid.NewGuid()

    (runtime.ForActor principal)
        .Management.SetEnabled(disableEvent, holder, false, CancellationToken.None)
    |> await
    |> appliedManagement disableEvent

    Expect.isFalse
        (checkpointHolderEnabled owner action.CheckpointSigningKeyId)
        "Registered checkpoint holder is disabled."

    Expect.isFalse
        (checkpointHolderEnabled app action.CheckpointSigningKeyId)
        "App observes disabled checkpoint holder."

    let disabledRequest =
        { action with
            ApprovalId = Guid.NewGuid()
        }

    Expect.notEqual
        disabledRequest.ApprovalId
        action.ApprovalId
        "A new approval identity is tested."

    let beforeDenied = witness.Snapshot().TipSequence

    Expect.equal
        (approve principal disabledRequest)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "Disabled checkpoint holder cannot back a new owner approval."

    Expect.equal
        (witness.Snapshot().TipSequence)
        beforeDenied
        "Disabled-holder refusal appends no witness intent."

let private auditAndTamper
    app
    (witness: WitnessProtocol)
    (connection: NpgsqlConnection)
    (action: WriterHandoffApprovalRequest)
    =

    use stored =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.writer_handoff_approvals WHERE approval_id=@approval",
            connection
        )

    Sql.uuid stored "approval" action.ApprovalId
    Expect.equal (stored.ExecuteScalar() :?> int64) 1L "One exact approval row is retained."

    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let audited = DataAudit.run audit witness CancellationToken.None |> await

    Expect.equal
        audited.WriterHandoffApprovals
        1L
        "Full audit replays the witnessed owner approval."

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.writer_handoff_approvals "
            + "SET inventory_sha256=decode(repeat('ff',32),'hex') WHERE approval_id=@approval",
            connection
        )

    Sql.uuid tamper "approval" action.ApprovalId
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic approval was altered."

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit witness CancellationToken.None |> await |> ignore)
        "Full audit rejects an altered handoff inventory commitment."

let private ownerApproval owner app writer (witness: WitnessProtocol) =
    let principal = human "handoff-owner"
    let holder = human "handoff-checkpoint-holder"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal holder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let key, _, keyId, _ =
        registeredSigner runtime principal holder CopySignerPurpose.Checkpoint witness connection

    use key = key
    let action = request keyId (witness.Snapshot())
    denyUntrusted runtime witness holder action
    denyInvalid runtime witness principal action
    approveAndRetry runtime witness principal holder action
    disableAndDeny runtime witness owner app principal holder action
    auditAndTamper app witness connection action

let tests =
    testList
        "writer handoff approval"
        [
            testCase
                "[CC-BACKUP-001] owner approval is actor-bound witnessed and exact-retry safe"
                (fun _ -> withAuthorityRuntimeDatabase ownerApproval)
        ]
