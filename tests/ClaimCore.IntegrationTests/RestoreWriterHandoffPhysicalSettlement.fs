module internal ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalSettlement

open System.Threading
open System
open System.Security.Cryptography
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalWalRetention
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext
open ClaimCore.IntegrationTests.RestoreWriterHandoffPrivateFixture
open ClaimCore.IntegrationTests.RestoreWriterHandoffQualificationFixture
open ClaimCore.IntegrationTests.RestoreWriterHandoffTypedFixture

[<NoEquality; NoComparison>]
type PhysicalW1Settlement =
    {
        Capture: PhysicalCopyCapture
        Registered: RegisteredWalCapture
        Facts: RestoredPairFacts
        Access: RestoredPairAccess
        Containers: string * string
        Input: RestoreProduceInput
        Produced: SignedRestoreProduction
        CheckpointKey: Key
        KeyId: Guid
        Witness: WitnessProtocol
        Custody: IKeyCustody
        Suppression: SuppressionKeyFile
        Root: string
        ReportPath: string
        ReportSignaturePath: string
        IndexPath: string
        FencePath: string
        FenceSignaturePath: string
        NewCapabilityPath: string
        Fence: byte array
        Prepare: byte array
        PrepareSignature: byte array
        PreparePath: string
        PrepareSignaturePath: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        HandoffId: Guid
        NewCapability: byte array
    }

let private signedFence (value: PhysicalW1Settlement) =
    let before =
        (value.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    Expect.throws
        (fun () ->
            verifyProspectiveFence
                value.Access
                value.Input
                value.Produced
                value.Custody
                value.Suppression
                value.Prepare
                value.Fence
                Array.empty
            |> ignore)
        "Unsigned prospective fence cannot qualify W1 PREPARE"

    Expect.equal
        ((value.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Missing independent fence signature appended no witness authority"

    waitForFenceObservation value.CheckedAt
    let signed = SignatureAlgorithm.Ed25519.Sign(value.CheckpointKey, value.Fence)

    match PrivateFileService.writeNew 64 value.FenceSignaturePath signed with
    | Ok() -> ()
    | Error _ -> failtest "Post-isolation W1 fence signature was refused"

    verifyProspectiveFence
        value.Access
        value.Input
        value.Produced
        value.Custody
        value.Suppression
        value.Prepare
        value.Fence
        signed
    |> ignore

    DatabaseWriterHandoffExecution.run
        value.Access.Owner
        value.PreparePath
        value.PrepareSignaturePath
        "PREPARE"
    |> requireClosedOwnerCommand

    Expect.equal
        ((value.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Root-closed owner command appended no witness authority"

    signed

let private typedSettlement (value: PhysicalW1Settlement) signedFence =
    let settledId, sequence, hash =
        RestoreWriterHandoffTypedFixture.run
            {
                Access = value.Access
                Witness = value.Witness
                Custody = value.Custody
                Suppression = value.Suppression
                Input = value.Input
                Produced = value.Produced
                CheckpointKey = value.CheckpointKey
                KeyId = value.KeyId
                Prepare = value.Prepare
                PrepareSignature = value.PrepareSignature
                Fence = value.Fence
                FenceSignature = signedFence
                NewCapability = value.NewCapability
                ValidUntil = value.ValidUntil
            }

    Expect.equal settledId value.HandoffId "Typed W1 settlement kept the exact handoff ID"

    Expect.equal
        ((value.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).WriterGeneration)
        (value.Facts.WriterGeneration + 1L)
        "W1 advanced the exact restored writer generation"

    Expect.isTrue
        ((value.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).ActivationPending)
        "W1 SETTLE leaves case work activation-pending"

    requireRegisteredHorizonSegments value.Capture value.Registered value.Containers
    settledId, sequence, hash

let settle onSettled (value: PhysicalW1Settlement) =
    withOwnerEnvironment
        value.Root
        value.Access
        value.Capture.ArchiveRoot
        value.ReportPath
        value.ReportSignaturePath
        value.IndexPath
        value.FencePath
        value.FenceSignaturePath
        value.NewCapabilityPath
        (fun () ->
            let signed = signedFence value
            let settledId, sequence, hash = typedSettlement value signed

            onSettled
                {
                    Capture = value.Capture
                    Registered = value.Registered
                    Facts = value.Facts
                    Access = value.Access
                    Containers = value.Containers
                    Input = value.Input
                    Report = value.Produced
                    CheckpointKey = value.CheckpointKey
                    Witness = value.Witness
                    HandoffId = settledId
                    W1Sequence = sequence
                    W1Hash = hash
                    Fence = value.Fence
                    FenceSignature = signed
                    NewCapability = value.NewCapability
                    ValidUntil = value.ValidUntil
                })
