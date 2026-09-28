module internal ClaimCore.IntegrationTests.WriterHandoffProtocolPreparation

open System
open System.Security.Cryptography
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.WriterHandoffApprovalTests
open ClaimCore.IntegrationTests.WriterHandoffDocuments
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.WriterHandoffSyntheticVerifier
open ClaimCore.IntegrationTests.FixturePrivateFiles

let private approvePair (runtime: Runtime) (witness: WitnessProtocol) first second keyId newHash =
    let reviewed = witness.Snapshot()
    let handoffId = Guid.NewGuid()
    let template = request keyId reviewed

    let firstApproval =
        { template with
            HandoffId = handoffId
            NewCapabilitySha256 = newHash
        }

    let secondApproval =
        { firstApproval with
            ApprovalId = Guid.NewGuid()
        }

    approve runtime first firstApproval
    approve runtime second secondApproval
    handoffId, reviewed, firstApproval, secondApproval

let private document
    (witness: WitnessProtocol)
    handoffId
    reviewed
    (firstApproval: WriterHandoffApprovalRequest)
    (secondApproval: WriterHandoffApprovalRequest)
    newHash
    =
    let current = witness.Snapshot()
    let report = SHA256.HashData(Array.create 32 0x74uy)
    let validUntil = DateTimeOffset.UtcNow.AddMinutes(10.)

    let canonical =
        prepare
            witness.Identity
            handoffId
            current.WriterGeneration
            reviewed.TipSequence
            reviewed.TipHash
            current.TipSequence
            current.TipHash
            newHash
            firstApproval.CheckpointSigningKeyId
            firstApproval.FenceReportSha256
            firstApproval.InventorySha256
            report
            firstApproval.ApprovalId
            secondApproval.ApprovalId
            validUntil

    current, report, validUntil, canonical

let private ownerPrepare
    (primary: NpgsqlConnection)
    app
    writer
    (witness: WitnessProtocol)
    (value: WriterHandoffPreparation)
    canonical
    signature
    oldCapability
    newCapability
    =
    let ownerWitness = witnessOwnerFor writer
    use source = RuntimeDataSource.create app
    let qualifier = WriterHandoffSyntheticVerifier.create value

    match
        WriterHandoffOwnerPreparation.prepare
            primary
            source
            ownerWitness
            witness
            qualifier
            (Some(syntheticCommitments witness.Identity))
            canonical
            signature
            oldCapability
            newCapability
        |> await
    with
    | WriterHandoffOwnerOutcome.Prepared(id, _, _) when id = value.HandoffId -> ()
    | _ -> failtest "Synthetic owner PREPARE was not confirmed."

    ownerWitness, qualifier

let private context
    value
    canonical
    signature
    ticket
    ownerWitness
    (firstApproval: WriterHandoffApprovalRequest)
    report
    newHash
    validUntil
    qualifier
    =
    {
        Value = value
        Canonical = canonical
        Signature = signature
        Ticket = ticket
        OwnerWitness = ownerWitness
        Fence = firstApproval.FenceReportSha256
        Inventory = firstApproval.InventorySha256
        Report = report
        NewCapabilityHash = newHash
        ValidUntil = validUntil
        Verifier = qualifier
    }

let private intentTicket (witness: WitnessProtocol) handoffId =
    witness.EvidenceStore.TryReadEvidence(handoffId, Intent)
    |> Option.map _.Ticket
    |> Option.defaultWith (fun () -> failtest "Confirmed handoff INTENT is absent.")

let prepareHandoff
    (runtime: Runtime)
    (witness: WitnessProtocol)
    (primary: NpgsqlConnection)
    owner
    app
    writer
    first
    second
    keyId
    (key: Key)
    (algorithm: SignatureAlgorithm)
    (oldCapability: byte array)
    (newCapability: byte array)
    =
    let newHash = SHA256.HashData(newCapability)

    let handoffId, reviewed, firstApproval, secondApproval =
        approvePair runtime witness first second keyId newHash

    let current, report, validUntil, canonical =
        document witness handoffId reviewed firstApproval secondApproval newHash

    let value = WriterHandoffPreparation.parse canonical |> Option.get
    let signature = algorithm.Sign(key, canonical)

    let ownerWitness, qualifier =
        ownerPrepare
            primary
            app
            writer
            witness
            value
            canonical
            signature
            oldCapability
            newCapability

    let ticket = intentTicket witness handoffId

    Expect.equal ticket.Sequence (current.TipSequence + 1L) "PREPARE reserves one tip."
    Expect.isTrue (witness.Snapshot().HandoffPending) "PREPARE fences every writer."
    assertPending owner app writer witness runtime first

    context
        value
        canonical
        signature
        ticket
        ownerWitness
        firstApproval
        report
        newHash
        validUntil
        qualifier
