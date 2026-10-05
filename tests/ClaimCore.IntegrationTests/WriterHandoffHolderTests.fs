module ClaimCore.IntegrationTests.WriterHandoffHolderTests

open System
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
open ClaimCore.IntegrationTests.WriterHandoffApprovalTests

let private changeCustodianGrant (runtime: Runtime) owner holder active =
    let eventId = Guid.NewGuid()

    (runtime.ForActor owner)
        .Management.SetGrant(
            eventId,
            holder,
            Role.AuditorCustodian,
            GrantTarget.Installation,
            active,
            CancellationToken.None
        )
    |> await
    |> appliedManagement eventId

let private retireCheckpointSigner runtime owner holder keyId digest connection witness =
    let first, second =
        approvePair
            runtime
            owner
            holder
            keyId
            digest
            CopySignerAction.Retire
            CopySignerPurpose.Checkpoint

    let retireId = Guid.NewGuid()

    ManagedCopySignerAdministration.retire
        connection
        witness
        retireId
        keyId
        CopySignerPurpose.Checkpoint
        first
        second
    |> await
    |> appliedSigner retireId


let private holderAuthority ownerConnection app writer (witness: WitnessProtocol) =
    let owner = human "handoff-holder-owner"
    let holder = human "handoff-holder-custodian"
    provision ownerConnection witness owner |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime owner holder
    use connection = new NpgsqlConnection(ownerConnection)
    connection.Open()

    let key, _, keyId, digest =
        registeredSigner runtime owner holder CopySignerPurpose.Checkpoint witness connection

    use key = key

    let approve action =
        (runtime.ForActor owner).ApproveWriterHandoff(action, CancellationToken.None)
        |> await

    changeCustodianGrant runtime owner holder false

    let beforeGrant = (witness.Snapshot(CancellationToken.None) |> await).TipSequence

    let deniedGrant =
        request keyId ((witness.Snapshot(CancellationToken.None) |> await))

    Expect.equal
        (approve deniedGrant)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "Revoked checkpoint custodian grant refuses fresh handoff approval."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None) |> await).TipSequence)
        beforeGrant
        "Grant refusal creates no approval."

    changeCustodianGrant runtime owner holder true

    retireCheckpointSigner runtime owner holder keyId digest connection witness

    let beforeRetired = (witness.Snapshot(CancellationToken.None) |> await).TipSequence

    let deniedRetired =
        request keyId ((witness.Snapshot(CancellationToken.None) |> await))

    Expect.equal
        (approve deniedRetired)
        WriterHandoffApprovalOutcome.ResourceUnavailable
        "Retired CHECKPOINT signer refuses new handoff approval."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None) |> await).TipSequence)
        beforeRetired
        "Retired-key refusal adds no intent."

let tests =
    testList
        "writer handoff holder"
        [
            testCase
                "[CC-BACKUP-001] revoked custodian or retired checkpoint key cannot back approval"
                (fun _ -> withAuthorityRuntimeDatabase holderAuthority)
        ]
