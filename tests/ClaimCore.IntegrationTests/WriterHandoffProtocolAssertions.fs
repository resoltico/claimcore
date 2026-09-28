module internal ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions

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
open ClaimCore.IntegrationTests.WriterHandoffProcessFixture

let witnessOwnerFor (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let secondOwner (runtime: Runtime) first second =
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

let approve (runtime: Runtime) actor action =
    match
        (runtime.ForActor actor).ApproveWriterHandoff(action, CancellationToken.None)
        |> await
    with
    | WriterHandoffApprovalOutcome.Approved(id, _) when id = action.ApprovalId -> ()
    | _ -> failtest "Synthetic owner handoff approval failed."

let assertPending owner app writer witness (runtime: Runtime) principal =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let summary = DataAudit.run audit witness CancellationToken.None |> await
    Expect.equal summary.WriterHandoffPreparations 1L "Primary PREPARE is fully audited."
    Expect.equal summary.PendingIntents 1L "Witness PREPARE remains explicitly pending."
    verifyData owner writer witness "VERIFIED_WITH_PENDING_INTENTS" "1" "0" "1"

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            runtime.ForActor(principal).Definition(CancellationToken.None)
            |> await
            |> ignore)
        "Already-open old runtime denies disclosure while handoff is pending."

let assertCompleted owner app writer witness (runtime: Runtime) principal =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let summary = DataAudit.run audit witness CancellationToken.None |> await
    Expect.equal summary.WriterHandoffPreparations 1L "Preparation is retained."
    Expect.equal summary.WriterHandoffs 1L "Completed handoff is fully audited."
    Expect.equal summary.PendingIntents 0L "Exact handoff settlement closes pending intent."
    verifyData owner writer witness "VERIFIED" "1" "1" "0"

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            runtime.ForActor(principal).Definition(CancellationToken.None)
            |> await
            |> ignore)
        "Old already-open runtime stays fenced after capability rotation."

[<NoEquality; NoComparison>]
type PreparedSyntheticHandoff =
    {
        Value: WriterHandoffPreparation
        Canonical: byte array
        Signature: byte array
        Ticket: Ticket
        OwnerWitness: string
        Fence: byte array
        Inventory: byte array
        Report: byte array
        NewCapabilityHash: byte array
        ValidUntil: DateTimeOffset
        Verifier: IWriterHandoffEvidenceVerifier
    }
