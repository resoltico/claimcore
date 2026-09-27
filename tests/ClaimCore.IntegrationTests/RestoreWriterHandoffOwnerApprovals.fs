module internal ClaimCore.IntegrationTests.RestoreWriterHandoffOwnerApprovals

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.WriterHandoffApprovalTests
open ClaimCore.TestSupport

let restoredWitness (access: RestoredPairAccess) (identity: Identity) keyId =
    let material = witnessKey ()
    let custody = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(material)
    new WitnessProtocol(Store.OpenAudit(access.WitnessAudit, identity), custody, identity)

let approveOwners
    (access: RestoredPairAccess)
    (witness: WitnessProtocol)
    (inventorySha: string)
    (fenceSha: string)
    checkpointKeyId
    newCapabilitySha
    validUntil
    =
    let first = human "physical-restore-owner"
    let second = human "physical-report-second-owner"
    let reviewed = witness.Snapshot()
    let handoffId = Guid.NewGuid()
    let template = request checkpointKeyId reviewed

    let approvalOne =
        { template with
            HandoffId = handoffId
            NewCapabilitySha256 = newCapabilitySha
            FenceReportSha256 = Convert.FromHexString(fenceSha)
            InventorySha256 = Convert.FromHexString(inventorySha)
            ExpiresAt = validUntil
        }

    let approvalTwo =
        { approvalOne with
            ApprovalId = Guid.NewGuid()
        }

    let approve actor value (runtime: Runtime) =
        match
            (runtime.ForActor actor).ApproveWriterHandoff(value, CancellationToken.None)
            |> await
        with
        | WriterHandoffApprovalOutcome.Approved(id, revision) when
            id = value.ApprovalId && revision > 0L
            ->
            ()
        | _ -> failtest "Exact synthetic W1 owner approval was not witnessed"

    let runtime = openRuntime access.App access.WitnessWriter

    try
        approve first approvalOne runtime
        approve second approvalTwo runtime
    finally
        (runtime :> IDisposable).Dispose()

    Expect.throws
        (fun () -> (runtime.ForActor first).Definition(CancellationToken.None) |> await |> ignore)
        "Disposed old casework runtime cannot remain an active endpoint"

    let after = witness.Snapshot()

    Expect.equal
        (after.TipSequence - reviewed.TipSequence)
        4L
        "Only two exact owner approvals advanced the pre-W1 tip"

    handoffId, reviewed, after, approvalOne.ApprovalId, approvalTwo.ApprovalId
