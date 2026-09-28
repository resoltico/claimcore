module ClaimCore.IntegrationTests.WriterHandoffRevocationTests

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

let private grantSecondOwner (runtime: Runtime) first second =
    let registerId = Guid.NewGuid()

    (runtime.ForActor first).Management.RegisterActor(registerId, second, CancellationToken.None)
    |> await
    |> appliedManagement registerId

    let grantId = Guid.NewGuid()

    (runtime.ForActor first)
        .Management.SetGrant(
            grantId,
            second,
            Role.Owner,
            GrantTarget.Installation,
            true,
            CancellationToken.None
        )
    |> await
    |> appliedManagement grantId

let private revokeOwner owner app writer (witness: WitnessProtocol) =
    let first = human "handoff-revoked-owner"
    let second = human "handoff-continuing-owner"
    let holder = human "handoff-revocation-checkpoint-holder"
    provision owner witness first |> applied
    use runtime = openRuntime app writer
    grantSecondOwner runtime first second
    grantCustodian runtime first holder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let key, _, keyId, _ =
        registeredSigner runtime first holder CopySignerPurpose.Checkpoint witness connection

    use key = key
    let action = request keyId (witness.Snapshot())

    match
        (runtime.ForActor first).ApproveWriterHandoff(action, CancellationToken.None)
        |> await
    with
    | WriterHandoffApprovalOutcome.Approved(id, _) when id = action.ApprovalId -> ()
    | _ -> failtest "Synthetic first-owner approval failed."

    let revokeId = Guid.NewGuid()

    (runtime.ForActor second)
        .Management.SetGrant(
            revokeId,
            first,
            Role.Owner,
            GrantTarget.Installation,
            false,
            CancellationToken.None
        )
    |> await
    |> appliedManagement revokeId

    let before = witness.Snapshot().TipSequence

    for actor in [ first; second ] do
        Expect.equal
            ((runtime.ForActor actor).ApproveWriterHandoff(action, CancellationToken.None)
             |> await)
            WriterHandoffApprovalOutcome.ResourceUnavailable
            "Revoked owner or changed actor cannot replay another owner’s approval."

    Expect.equal (witness.Snapshot().TipSequence) before "Denied replay appends no witness event."

    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let result = DataAudit.run audit witness CancellationToken.None |> await

    Expect.equal
        result.WriterHandoffApprovals
        1L
        "Historical approval remains auditable after revocation."

let tests =
    testList
        "writer handoff revocation"
        [
            testCase
                "[CC-BACKUP-001] revoked owner cannot repeat approval but history remains audited"
                (fun _ -> withAuthorityRuntimeDatabase revokeOwner)
        ]
