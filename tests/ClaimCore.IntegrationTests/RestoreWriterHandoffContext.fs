module internal ClaimCore.IntegrationTests.RestoreWriterHandoffContext

open System
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections

/// Borrowed synthetic state is valid only inside the scoped callback that owns both containers.
[<NoEquality; NoComparison>]
type SettledW1Context =
    {
        Capture: PhysicalCopyCapture
        Registered: RegisteredWalCapture
        Facts: RestoredPairFacts
        Access: RestoredPairAccess
        Containers: string * string
        Input: RestoreProduceInput
        Report: SignedRestoreProduction
        CheckpointKey: Key
        Witness: WitnessProtocol
        HandoffId: Guid
        W1Sequence: int64
        W1Hash: byte array
        Fence: byte array
        FenceSignature: byte array
        NewCapability: byte array
        ValidUntil: DateTimeOffset
    }
