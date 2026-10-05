module ClaimCore.IntegrationTests.RealDataActivationMechanicsTests

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
open ClaimCore.IntegrationTests.RealDataActivationMechanicsSupport
open ClaimCore.IntegrationTests.RealDataActivationPlanFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests

let private outcome =
    function
    | InstallationUseActivationOutcome.Activated(id, sequence, hash) -> id, sequence, hash
    | _ -> failtest "Synthetic witnessed one-way activation was not definite."

let private refused =
    function
    | InstallationUseActivationOutcome.Refused -> ()
    | _ -> failtest "Rejected synthetic activation must have a definite refusal."

let private requireLossOwnerPrerequisite
    activate
    qualified
    profile
    plan
    (witness: WitnessProtocol)
    =
    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    activate qualified profile plan |> refused

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Real-data activation without prepositioned loss owners appended no authority."

let private mechanics owner app writer (witness: WitnessProtocol) profile =
    let first = human "activation-mechanics-one"
    let second = human "activation-mechanics-two"
    provision owner witness first |> applied
    owners app witness first second
    let planId, activationId, plan = publishSyntheticPlan owner witness profile

    let firstId, secondId =
        approvePair app writer witness first second planId activationId plan

    let qualified =
        proof plan ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))

    let ownerWitness = witnessOwnerFor writer
    use primary = new NpgsqlConnection(owner)
    primary.Open()

    let syntheticHealth _ _ _ _ _ _ =
        System.Threading.Tasks.Task.FromResult(())

    let activate value selectedProfile selectedPlan =
        InstallationUseActivationOwner.activateWithReviewedProfile
            selectedProfile
            syntheticHealth
            primary
            ownerWitness
            witness
            value
            selectedPlan
            firstId
            secondId
            CancellationToken.None
        |> await

    requireLossOwnerPrerequisite activate qualified profile plan witness

    registerLossOwners owner app writer witness first second

    let qualified =
        proof plan ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))

    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let expired =
        { qualified with
            ValidUntil = qualified.CheckedAtDatabase
        }

    activate expired profile plan |> refused

    let wrongProfile =
        { profile with
            PublicationRootKey = Array.create 32 0xEEuy
        }

    activate qualified wrongProfile plan |> refused

    let altered =
        { plan with
            Canonical = Array.append plan.Canonical [| 0uy |]
        }

    activate qualified profile altered |> refused

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Denied activation appended no ticket."

    let eventId, sequence, hash = activate qualified profile plan |> outcome
    Expect.equal eventId activationId "Activation reused published deterministic identity."
    Expect.equal sequence (before + 2L) "One exact witness INTENT and settlement were appended."

    let paired =
        (InstallationUseScopeRead.requirePair primary witness CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    Expect.equal paired.Phase InstallationUsePhase.Active "Both clusters released together."
    Expect.equal paired.ActivationHash (Some hash) "Both clusters retain exact ticket hash."

    activate qualified profile plan |> refused

    let replay =
        InstallationUseActivationReconcile.run primary witness CancellationToken.None
        |> await
        |> outcome

    Expect.equal (let id, _, _ = replay in id) eventId "Exact historical repair read back event."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        sequence
        "Exact retry added no witness event."

    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    DataAudit.run audit witness CancellationToken.None |> await |> ignore

let private stageWitnessActivation
    (primary: NpgsqlConnection)
    (witness: WitnessProtocol)
    writer
    planId
    activationId
    plan
    qualified
    firstId
    secondId
    (snapshot: Snapshot)
    =
    use transaction = primary.BeginTransaction()

    let published =
        InstallationUsePlanRead.verified primary transaction witness planId CancellationToken.None
        |> await
        |> Option.defaultWith (fun () -> failtest "Published synthetic plan vanished.")

    let approvals =
        InstallationUseActivationApprovals.verify
            primary
            transaction
            witness
            published
            firstId
            secondId
            DateTimeOffset.UtcNow
            CancellationToken.None
        |> await

    transaction.Rollback()

    let canonical =
        InstallationUseActivationCandidate.encodeFinal plan qualified approvals

    let ownerWitness = witnessOwnerFor writer

    let intent, settled =
        InstallationUseActivationWitness.activate
            ownerWitness
            witness
            activationId
            snapshot.TipSequence
            snapshot.TipHash
            canonical
            CancellationToken.None
        |> await

    Expect.equal (fst settled) (fst intent + 1L) "Witness W2 settled atomically."
    settled

let private witnessSettledPrimaryMissing owner app writer (witness: WitnessProtocol) profile =
    let first = human "activation-crash-one"
    let second = human "activation-crash-two"
    provision owner witness first |> applied
    owners app witness first second
    let planId, activationId, plan = publishSyntheticPlan owner witness profile

    let firstId, secondId =
        approvePair app writer witness first second planId activationId plan

    let snapshot = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let qualified = proof plan snapshot
    use primary = new NpgsqlConnection(owner)
    primary.Open()

    let settled =
        stageWitnessActivation
            primary
            witness
            writer
            planId
            activationId
            plan
            qualified
            firstId
            secondId
            snapshot

    Expect.throws
        (fun () ->
            (InstallationUseScopeRead.requirePair primary witness CancellationToken.None)
                .GetAwaiter()
                .GetResult()
            |> ignore)
        "Witness-only ACTIVE cannot open a case-work pair."

    let eventId, sequence, _ =
        InstallationUseActivationReconcile.run primary witness CancellationToken.None
        |> await
        |> outcome

    Expect.equal eventId activationId "Repair kept original operation identity."
    Expect.equal sequence (fst settled) "Repair used original witness ticket."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        (fst settled)
        "Primary repair did not append a second witness event."

    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    DataAudit.run audit witness CancellationToken.None |> await |> ignore

let tests =
    testList
        "real-data activation mechanics"
        [
            testCase "[CC-DB-001] witnessed owner activation is one-way and exactly reconcilable"
            <| fun _ -> withRealDataBootstrap mechanics
            testCase "[CC-DB-001] settled witness activation repairs a missing primary projection"
            <| fun _ -> withRealDataBootstrap witnessSettledPrimaryMissing
        ]
