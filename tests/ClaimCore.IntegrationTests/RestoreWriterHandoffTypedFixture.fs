module internal ClaimCore.IntegrationTests.RestoreWriterHandoffTypedFixture

open System
open System.IO
open System.Security.Cryptography
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestoreWriterHandoffQualificationFixture
open ClaimCore.IntegrationTests.RestoreWriterHandoffTypedSettlement
open ClaimCore.TestSupport

[<NoEquality; NoComparison>]
type TypedHandoffInput =
    {
        Access: RestoredPairAccess
        Witness: WitnessProtocol
        Custody: IKeyCustody
        Suppression: SuppressionKeyFile
        Input: RestoreProduceInput
        Produced: SignedRestoreProduction
        CheckpointKey: Key
        KeyId: Guid
        Prepare: byte array
        PrepareSignature: byte array
        Fence: byte array
        FenceSignature: byte array
        NewCapability: byte array
        ValidUntil: DateTimeOffset
    }

let private oldCapability () =
    let path =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Old synthetic writer capability is absent")

    File.ReadAllBytes(path)

let private writerProtocol (value: TypedHandoffInput) oldBytes =
    let material = witnessKey ()
    let custody = new KeyRing(value.KeyId, [ value.KeyId, material ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(material)

    new WitnessProtocol(
        new Store(value.Access.WitnessWriter, value.Witness.Identity, oldBytes),
        custody,
        value.Witness.Identity
    )

let private requirePrepared
    owner
    source
    (value: TypedHandoffInput)
    writer
    qualifier
    commitments
    oldBytes
    (proposal: WriterHandoffPreparation)
    =
    match
        WriterHandoffOwnerPreparation.prepare
            owner
            source
            value.Access.WitnessOwner
            writer
            qualifier
            (Some commitments)
            value.Prepare
            value.PrepareSignature
            oldBytes
            value.NewCapability
        |> await
    with
    | WriterHandoffOwnerOutcome.Prepared(id, _, _) when id = proposal.HandoffId -> ()
    | _ -> failtest "Typed synthetic W1 PREPARE did not complete"

let private withPreparedOwner (value: TypedHandoffInput) oldBytes action =
    let qualifier =
        verifyProspectiveFence
            value.Access
            value.Input
            value.Produced
            value.Custody
            value.Suppression
            value.Prepare
            value.Fence
            value.FenceSignature

    let proposal =
        WriterHandoffPreparation.parse value.Prepare
        |> Option.defaultWith (fun () -> failtest "Exact W1 PREPARE bytes are invalid")

    use owner = new NpgsqlConnection(value.Access.Owner)
    owner.Open()
    let identity, suppressionId, check = DatabaseVerifyData.identity owner

    let commitments =
        DatabaseVerifyData.commitments value.Suppression identity suppressionId check

    use source = RuntimeDataSource.create value.Access.App
    use writer = writerProtocol value oldBytes
    requirePrepared owner source value writer qualifier commitments oldBytes proposal
    action owner source commitments proposal

let run (value: TypedHandoffInput) =
    let oldBytes = oldCapability ()

    try
        withPreparedOwner value oldBytes (fun owner source commitments proposal ->
            finish
                owner
                source
                value.Access
                value.Witness
                value.Custody
                value.Suppression
                value.Input
                value.Produced
                value.CheckpointKey
                commitments
                proposal
                value.Prepare
                value.Fence
                value.FenceSignature
                oldBytes
                value.NewCapability
                value.ValidUntil)
    finally
        CryptographicOperations.ZeroMemory(oldBytes)
