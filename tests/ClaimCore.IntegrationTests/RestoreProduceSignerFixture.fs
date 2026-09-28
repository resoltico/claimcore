module internal ClaimCore.IntegrationTests.RestoreProduceSignerFixture

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let private addOwner (runtime: Runtime) =
    let first = human "physical-restore-owner"
    let second = human "physical-report-second-owner"
    let management = (runtime.ForActor first).Management
    let registerId = Guid.NewGuid()

    management.RegisterActor(registerId, second, CancellationToken.None)
    |> await
    |> appliedManagement registerId

    let grantId = Guid.NewGuid()

    management.SetGrant(
        grantId,
        second,
        Role.Owner,
        GrantTarget.Installation,
        true,
        CancellationToken.None
    )
    |> await
    |> appliedManagement grantId

let reportAuthorities owner app writer (witness: WitnessProtocol) =
    let first = human "physical-restore-owner"
    let reportHolder = human "physical-report-holder"
    let checkpointHolder = human "physical-checkpoint-holder"
    use runtime = openRuntime app writer
    addOwner runtime
    grantCustodian runtime first reportHolder
    grantCustodian runtime first checkpointHolder
    use source = new NpgsqlConnection(owner)
    source.Open()

    let reportKey, _, reportKeyId, _ =
        registeredSigner runtime first reportHolder CopySignerPurpose.RestoreReport witness source

    try
        let checkpointKey, _, checkpointKeyId, _ =
            registeredSigner
                runtime
                first
                checkpointHolder
                CopySignerPurpose.Checkpoint
                witness
                source

        reportKey, reportKeyId, checkpointKey, checkpointKeyId
    with _ ->
        reportKey.Dispose()
        reraise ()
