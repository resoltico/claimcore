module ClaimCore.IntegrationTests.ManagedCopySignerPurposeTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let private denied (runtime: Runtime) principal request (witness: WitnessProtocol) category =
    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let result =
        (runtime.ForActor principal).ApproveCopySigner(request, CancellationToken.None)
        |> await

    Expect.equal result CopySignerApprovalOutcome.ResourceUnavailable category

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Refused approval creates no witness event."

let private holderRequest keyId digest purpose =
    approval keyId digest CopySignerAction.Register purpose CopySignerApprovalRole.Custodian

let private ownerRequest keyId digest purpose (holder: CopySignerApprovalRequest) =
    { approval
          keyId
          digest
          CopySignerAction.Register
          purpose
          (CopySignerApprovalRole.Owner holder.ApprovalId) with
        ExpiresAt = holder.ExpiresAt
    }

let private approvalRefusals owner app writer (witness: WitnessProtocol) =
    let principal = human "purpose-owner"
    let holderA = human "purpose-holder-a"
    let holderB = human "purpose-holder-b"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal holderA
    grantCustodian runtime principal holderB
    let keyA = Guid.NewGuid()
    let keyB = Guid.NewGuid()
    let digestA = SHA256.HashData(Array.create 32 0x42uy)
    let digestB = SHA256.HashData(Array.create 32 0x43uy)
    let approvedA = holderRequest keyA digestA CopySignerPurpose.CopyAttestor
    let approvedB = holderRequest keyB digestB CopySignerPurpose.LocationRegistry

    (runtime.ForActor holderA).ApproveCopySigner(approvedA, CancellationToken.None)
    |> await
    |> approved approvedA.ApprovalId

    (runtime.ForActor holderB).ApproveCopySigner(approvedB, CancellationToken.None)
    |> await
    |> approved approvedB.ApprovalId

    denied
        runtime
        principal
        (ownerRequest keyA digestA CopySignerPurpose.LocationInspector approvedA)
        witness
        "Changed purpose cannot reuse holder approval."

    denied
        runtime
        principal
        (ownerRequest keyA digestA CopySignerPurpose.CopyAttestor approvedB)
        witness
        "Swapped holder/key approval is refused."

    let management = (runtime.ForActor principal).Management
    let revokeId = Guid.NewGuid()

    management.SetGrant(
        revokeId,
        holderA,
        Role.AuditorCustodian,
        GrantTarget.Installation,
        false,
        CancellationToken.None
    )
    |> await
    |> appliedManagement revokeId

    denied
        runtime
        principal
        (ownerRequest keyA digestA CopySignerPurpose.CopyAttestor approvedA)
        witness
        "Revoked holder grant cannot authorize owner approval."

let private selfApproval owner app writer (witness: WitnessProtocol) =
    let principal = human "self-signer-owner"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    let management = (runtime.ForActor principal).Management
    let grantId = Guid.NewGuid()

    management.SetGrant(
        grantId,
        principal,
        Role.AuditorCustodian,
        GrantTarget.Installation,
        true,
        CancellationToken.None
    )
    |> await
    |> appliedManagement grantId

    let keyId = Guid.NewGuid()
    let digest = SHA256.HashData(Array.create 32 0x44uy)
    let held = holderRequest keyId digest CopySignerPurpose.CopyAttestor

    (runtime.ForActor principal).ApproveCopySigner(held, CancellationToken.None)
    |> await
    |> approved held.ApprovalId

    denied
        runtime
        principal
        (ownerRequest keyId digest CopySignerPurpose.CopyAttestor held)
        witness
        "One human cannot approve their own held signer key."

let private deletionGate owner app writer (witness: WitnessProtocol) =
    let principal = human "deletion-gate-owner"
    let verifier = human "deletion-gate-verifier"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal verifier
    use source = RuntimeDataSource.create app
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    let gate = new PostgresActorGate(source, commitments) :> IActorGate

    let admit actor =
        gate.Installation(actor, EndpointAction.ApproveCopyDeletion, CancellationToken.None)
        |> await

    Expect.isSome (admit verifier) "Current human verifier grant reaches deletion approval."
    Expect.isNone (admit principal) "Owner role alone cannot approve copy deletion."
    Expect.isNone (admit (human "unknown-delete-verifier")) "Unknown human has no ambient approval."
    Expect.isNone (admit (service "delete-automation")) "Service principal cannot approve deletion."
    let revokeId = Guid.NewGuid()

    (runtime.ForActor principal)
        .Management.SetGrant(
            revokeId,
            verifier,
            Role.AuditorCustodian,
            GrantTarget.Installation,
            false,
            CancellationToken.None
        )
    |> await
    |> appliedManagement revokeId

    Expect.isNone (admit verifier) "Revoked verifier cannot enter deletion approval."

let tests =
    testList
        "managed-copy signer purpose"
        [
            testCase
                "[CC-BACKUP-001] signer owner approval refuses swapped purpose holder and revoked grant"
                (fun _ -> withAuthorityRuntimeDatabase approvalRefusals)
            testCase "[CC-BACKUP-001] signer key holder cannot self-approve as owner" (fun _ ->
                withAuthorityRuntimeDatabase selfApproval)
            testCase
                "[CC-BACKUP-001] deletion approval gate admits only current human verifier grants"
                (fun _ -> withAuthorityRuntimeDatabase deletionGate)
        ]
