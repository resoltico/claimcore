module ClaimCore.IntegrationTests.WriterActivationCrashTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalTests
open ClaimCore.IntegrationTests.WriterActivationSyntheticTail
open ClaimCore.IntegrationTests.WriterActivationTests

let private witnessOnlyActivation (context: SettledW1Context) =
    let value =
        { evidence (syntheticTail context) with
            ValidUntil = utcMicrosecond (DateTimeOffset.UtcNow.AddSeconds(10.0))
        }

    let activationId = WriterActivationCandidate.activationId context.HandoffId
    let canonical = WriterActivationCandidate.encode value

    let ticket =
        try
            WriterActivationWitness.activate
                context.Access.WitnessOwner
                context.Witness
                context.HandoffId
                activationId
                context.W1Sequence
                context.W1Hash
                canonical
                CancellationToken.None
            |> await
        finally
            CryptographicOperations.ZeroMemory(canonical)

    use owner = new NpgsqlConnection(context.Access.Owner)
    owner.Open()
    Expect.isTrue (WriterActivationPrimary.current owner).Pending "Primary is still quarantined."

    match openRestored context with
    | Error _ -> ()
    | Ok runtime ->
        use unexpected = runtime
        failtest "Witness-only W2 cannot admit an unreconciled primary."

    value, activationId, ticket

let private reconcileAfterExpiry
    (context: SettledW1Context)
    (value: WriterActivationEvidence)
    activationId
    ticket
    =
    use owner = new NpgsqlConnection(context.Access.Owner)
    owner.Open()
    DatabaseObservation.afterInstant owner value.ValidUntil
    use dataSource = RuntimeDataSource.create context.Access.App
    let suppression = FixturePrivateFiles.syntheticCommitments context.Witness.Identity

    let outcome =
        WriterHandoffActivation.activate
            owner
            dataSource
            context.Access.WitnessOwner
            context.Witness
            (testVerifier None)
            (Some suppression)
            value
            CancellationToken.None
        |> await

    match outcome with
    | WriterActivationOutcome.Activated(id, sequence, hash) ->
        Expect.equal id activationId "Reconciliation preserves the exact W2 identity."
        Expect.equal sequence ticket.Settlement.Sequence "Reconciliation preserves W2 sequence."
        Expect.equal hash ticket.Settlement.EntryHash "Reconciliation preserves W2 hash."

        Expect.equal
            ((context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
            sequence
            "Reconciliation appends no second W2 event."

        requireAuditedActivation dataSource context suppression sequence
    | _ -> failtest "Expired witness-only W2 must reconcile its primary projection."

    match openRestored context with
    | Ok runtime ->
        use admitted = runtime
        ()
    | Error _ -> failtest "Exact reconciled W2 must admit the restored writer."

let tests =
    testList
        "restored writer activation reconciliation"
        [
            testCase
                "[CC-BACKUP-001] witness-only W2 reconciles after proof expiry without new authority"
                (fun _ ->
                    withAuthorityRuntimeDatabase (
                        withSettledPhysicalPair (fun context ->
                            let value, activationId, ticket = witnessOnlyActivation context
                            reconcileAfterExpiry context value activationId ticket)
                    ))
        ]
