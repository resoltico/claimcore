module internal ClaimCore.IntegrationTests.BackupCapturePhysicalSigners

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.BackupCapturePhysicalPrivate

let private register
    (runtime: Runtime)
    principal
    custodian
    purpose
    (key: SigningKeyFiles)
    (owner: NpgsqlConnection)
    (witness: WitnessProtocol)
    =
    let keyId = Guid.NewGuid()
    let eventId = Guid.NewGuid()
    let publicSha = SHA256.HashData key.PublicBytes

    let ownerApproval, custodianApproval =
        approvePair runtime principal custodian keyId publicSha CopySignerAction.Register purpose

    ManagedCopySignerAdministration.register
        owner
        witness
        eventId
        keyId
        purpose
        key.PublicBytes
        ownerApproval
        custodianApproval
    |> await
    |> appliedSigner eventId

    keyId

let registered owner app writer (witness: WitnessProtocol) copy checkpoint =
    let principal = human "physical-capture-owner"
    let copyHolder = human "physical-capture-copy-custodian"
    let checkpointHolder = human "physical-capture-checkpoint-custodian"
    provision owner witness principal |> applied

    use runtime = openRuntime app writer
    grantCustodian runtime principal copyHolder
    grantCustodian runtime principal checkpointHolder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let copyId =
        register runtime principal copyHolder CopySignerPurpose.CopyAttestor copy connection witness

    let checkpointId =
        register
            runtime
            principal
            checkpointHolder
            CopySignerPurpose.Checkpoint
            checkpoint
            connection
            witness

    copyId, checkpointId
