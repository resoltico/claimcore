module internal ClaimCore.IntegrationTests.WriterHandoffAbortScenario

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.WriterHandoffProtocolPreparation

[<NoEquality; NoComparison>]
type Scenario =
    {
        Owner: string
        App: string
        Writer: string
        Witness: WitnessProtocol
        Runtime: Runtime
        Primary: NpgsqlConnection
        First: PrincipalKey
        Second: PrincipalKey
        CheckpointHolder: PrincipalKey
        KeyOne: Key
        KeyTwo: Key
        KeyOneId: Guid
        KeyTwoId: Guid
        Algorithm: SignatureAlgorithm
        CheckpointKey: Key
        CheckpointId: Guid
        CheckpointAlgorithm: SignatureAlgorithm
        OldCapability: byte array
        NewCapability: byte array
    }

let private grantCustodianToOwner (runtime: Runtime) owner target =
    let eventId = Guid.NewGuid()

    (runtime.ForActor owner)
        .Management.SetGrant(
            eventId,
            target,
            Role.AuditorCustodian,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement eventId

let private capabilities () =
    let oldCapability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic writer capability path is absent.")
        |> File.ReadAllBytes

    oldCapability, RandomNumberGenerator.GetBytes(32)

let withScenario owner app writer (witness: WitnessProtocol) action =
    let first = human "abort-owner-one"
    let second = human "abort-owner-two"
    let checkpointHolder = human "abort-checkpoint-holder"
    provision owner witness first |> applied
    use runtime = openRuntime app writer
    secondOwner runtime first second
    grantCustodianToOwner runtime first first
    grantCustodianToOwner runtime first second
    use primary = new NpgsqlConnection(owner)
    primary.Open()

    let keyOne, algorithm, keyOneId, _ =
        registeredSigner runtime second first CopySignerPurpose.WriterHandoffAbort witness primary

    use keyOne = keyOne

    let keyTwo, _, keyTwoId, _ =
        registeredSigner runtime first second CopySignerPurpose.WriterHandoffAbort witness primary

    use keyTwo = keyTwo
    grantCustodian runtime first checkpointHolder

    let checkpointKey, checkpointAlgorithm, checkpointId, _ =
        registeredSigner runtime first checkpointHolder CopySignerPurpose.Checkpoint witness primary

    use checkpointKey = checkpointKey

    let oldCapability, newCapability = capabilities ()

    try
        action
            {
                Owner = owner
                App = app
                Writer = writer
                Witness = witness
                Runtime = runtime
                Primary = primary
                First = first
                Second = second
                CheckpointHolder = checkpointHolder
                KeyOne = keyOne
                KeyTwo = keyTwo
                KeyOneId = keyOneId
                KeyTwoId = keyTwoId
                Algorithm = algorithm
                CheckpointKey = checkpointKey
                CheckpointId = checkpointId
                CheckpointAlgorithm = checkpointAlgorithm
                OldCapability = oldCapability
                NewCapability = newCapability
            }
    finally
        CryptographicOperations.ZeroMemory(oldCapability)
        CryptographicOperations.ZeroMemory(newCapability)
