module internal ClaimCore.IntegrationTests.WriterHandoffProtocolCommit

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.WriterHandoffDocuments
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.FixturePrivateFiles

let private document (context: PreparedSyntheticHandoff) (witness: WitnessProtocol) =
    let value = context.Value

    let canonical =
        WriterHandoffDocuments.settlement
            witness.Identity
            value.HandoffId
            value.OldGeneration
            context.Ticket.Sequence
            context.Ticket.EntryHash
            (SHA256.HashData(context.Canonical))
            context.NewCapabilityHash
            value.CheckpointSigningKeyId
            context.Fence
            context.Inventory
            context.Report
            value.ApprovalOneId
            value.ApprovalTwoId
            context.ValidUntil

    Expect.isSome (WriterHandoffSettlement.parse canonical) "Signed COMMIT is canonical."
    canonical

let private ownerCommit
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    canonical
    signature
    oldCapability
    newCapability
    expected
    =
    match
        WriterHandoffOwnerSettlement.commit
            primary
            source
            context.OwnerWitness
            witness
            context.Verifier
            (Some(syntheticCommitments witness.Identity))
            canonical
            signature
            oldCapability
            newCapability
        |> await
    with
    | WriterHandoffOwnerOutcome.Settled(id, _, _) when id = context.Value.HandoffId -> ()
    | _ -> failtestf "%s" expected

let private simulateLostPrimaryCommit
    (context: PreparedSyntheticHandoff)
    (witness: WitnessProtocol)
    (runtime: Runtime)
    first
    canonical
    signature
    oldCapability
    newCapability
    =
    let parsed = WriterHandoffSettlement.parse canonical |> Option.get

    WriterHandoffWitnessCommands.commit
        context.OwnerWitness
        witness
        parsed
        context.Canonical
        canonical
        signature
        oldCapability
        newCapability
    |> ignore

    Expect.throwsT<InvalidOperationException>
        (fun () -> runtime.ForActor(first).Definition(CancellationToken.None) |> await |> ignore)
        "Witness W2 before primary P2 quarantines the already-open runtime."

let private rejectTamperedBackfill
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    canonical
    signature
    oldCapability
    newCapability
    =
    let tampered = Array.copy signature
    tampered[0] <- tampered[0] ^^^ 0x80uy

    let outcome =
        WriterHandoffOwnerSettlement.commit
            primary
            source
            context.OwnerWitness
            witness
            context.Verifier
            (Some(syntheticCommitments witness.Identity))
            canonical
            tampered
            oldCapability
            newCapability
        |> await

    match outcome with
    | WriterHandoffOwnerOutcome.Settled _ -> failtest "Tampered W2 proof backfilled P2."
    | _ -> ()

    use command =
        new NpgsqlCommand(
            "SELECT writer_generation FROM claimcore.installation_lineage WHERE singleton",
            primary
        )

    Expect.equal
        (command.ExecuteScalar() :?> int64)
        context.Value.OldGeneration
        "Rejected proof leaves primary at its old writer generation."

let private assertSettlement
    (context: PreparedSyntheticHandoff)
    (witness: WitnessProtocol)
    (runtime: Runtime)
    owner
    app
    writer
    first
    newCapability
    =
    let ticket =
        witness.EvidenceStore.TryReadEvidence(context.Value.HandoffId, SettledAuthority)
        |> Option.map _.Ticket
        |> Option.defaultWith (fun () -> failtest "Confirmed handoff settlement is absent.")

    Expect.equal
        ticket.Sequence
        (context.Ticket.Sequence + 1L)
        "COMMIT settles exact pending intent without a duplicate ticket."

    Expect.equal (witness.Snapshot().WriterGeneration) 2L "W2 rotated the generation."
    assertCompleted owner app writer witness runtime first
    use newStore = new Store(writer, witness.Identity, newCapability)
    let pending = newStore.Snapshot()
    Expect.equal pending.WriterGeneration 2L "The replacement capability names generation two."
    Expect.isTrue pending.ActivationPending "W2 remains quarantined before separate activation."

    Expect.throwsT<InvalidOperationException>
        (fun () -> newStore.Admit())
        "The replacement writer cannot serve case work until W3 activation."

    Expect.throwsT<InvalidOperationException>
        (fun () -> witness.Admit())
        "Old capability is no longer writer authority."

let private submitAndRetry
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    canonical
    signature
    oldCapability
    newCapability
    retry
    =
    ownerCommit
        context
        primary
        source
        witness
        canonical
        signature
        oldCapability
        newCapability
        "Synthetic owner COMMIT was not confirmed"

    if retry then
        ownerCommit
            context
            primary
            source
            witness
            canonical
            signature
            oldCapability
            newCapability
            "Exact P2 retry did not confirm settlement"

let commitHandoff
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    (witness: WitnessProtocol)
    (runtime: Runtime)
    owner
    app
    writer
    first
    (key: Key)
    (algorithm: SignatureAlgorithm)
    simulateCrash
    (oldCapability: byte array)
    (newCapability: byte array)
    =
    let canonical = document context witness
    let signature = algorithm.Sign(key, canonical)
    use source = RuntimeDataSource.create app

    if simulateCrash then
        simulateLostPrimaryCommit
            context
            witness
            runtime
            first
            canonical
            signature
            oldCapability
            newCapability

        rejectTamperedBackfill
            context
            primary
            source
            witness
            canonical
            signature
            oldCapability
            newCapability

    submitAndRetry
        context
        primary
        source
        witness
        canonical
        signature
        oldCapability
        newCapability
        simulateCrash

    assertSettlement context witness runtime owner app writer first newCapability
