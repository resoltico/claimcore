module internal ClaimCore.IntegrationTests.WriterHandoffAbortRetryAssertions

open System.Threading
open System
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FixturePrivateFiles
open ClaimCore.IntegrationTests.WriterHandoffProtocolAssertions
open ClaimCore.IntegrationTests.WriterHandoffProtocolPreparation


let private assertChangedA1
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    canonical
    signatureOne
    signatureTwo
    oldCapability
    afterA1
    =
    let altered = Array.copy signatureOne
    altered[0] <- altered[0] ^^^ 0x80uy

    match
        WriterHandoffOwnerAbortStage.start
            primary
            source
            context.OwnerWitness
            witness
            (Some(syntheticCommitments witness.Identity))
            canonical
            altered
            signatureTwo
            oldCapability
        |> await
    with
    | WriterHandoffAbortOutcome.AwaitingPrimary _
    | WriterHandoffAbortOutcome.Released _ -> failtest "Changed A1 signature was accepted."
    | _ -> ()

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        afterA1
        "A1 retries append no ticket."

let assertA1Retry
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    (value: WriterHandoffAbort)
    canonical
    signatureOne
    signatureTwo
    oldCapability
    =
    let afterA1 =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let exactTicket =
        WriterHandoffWitnessAbortCommands.abort
            context.OwnerWitness
            witness
            value
            canonical
            signatureOne
            signatureTwo
            oldCapability
            CancellationToken.None
        |> await

    Expect.equal exactTicket.Sequence afterA1 "Lost A1 response reuses exact ciphertext."

    match
        WriterHandoffOwnerAbortStage.start
            primary
            source
            context.OwnerWitness
            witness
            (Some(syntheticCommitments witness.Identity))
            canonical
            signatureOne
            signatureTwo
            oldCapability
        |> await
    with
    | WriterHandoffAbortOutcome.AwaitingPrimary(id, sequence, _) when
        id = value.HandoffId && sequence = afterA1
        ->
        ()
    | _ -> failtest "Exact A1 readback did not reconcile."

    assertChangedA1
        context
        primary
        source
        witness
        canonical
        signatureOne
        signatureTwo
        oldCapability
        afterA1

let assertMissingA2
    (context: PreparedSyntheticHandoff)
    (primary: NpgsqlConnection)
    source
    (witness: WitnessProtocol)
    canonical
    signatureOne
    signatureTwo
    oldCapability
    =
    match
        WriterHandoffOwnerAbortRelease.release
            primary
            source
            context.OwnerWitness
            witness
            (Some(syntheticCommitments witness.Identity))
            canonical
            signatureOne
            signatureTwo
            oldCapability
        |> await
    with
    | WriterHandoffAbortOutcome.Released _ -> failtest "A3 released without primary A2."
    | _ -> ()

    Expect.isTrue
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).HandoffPending)
        "Missing A2 remains quarantined."
