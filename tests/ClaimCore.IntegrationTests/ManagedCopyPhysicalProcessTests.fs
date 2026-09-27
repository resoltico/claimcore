module ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProofFixture
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestoreRegisteredWalCapture
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.RestoreProducePhysicalChecks
open ClaimCore.TestSupport

open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerRefusals

open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerTransition
open ClaimCore.IntegrationTests.BackupHealthCommitInterleaving
open ClaimCore.IntegrationTests.ManagedCopyPhysicalVerificationFixture


let internal withVerifiedCopies select callback owner app writer (witness: WitnessProtocol) =
    withCapturedPrimary
        (fun capture ->
            let verified =
                verifySelected
                    select
                    (fun _ _ _ _ _ -> ())
                    (fun _ _ -> ())
                    owner
                    app
                    writer
                    witness
                    capture

            callback capture verified)
        owner
        app
        writer
        witness

let internal withVerifiedRegisteredCopies callback owner app writer (witness: WitnessProtocol) =
    let mutable registered = None

    withVerifiedCopies
        (fun capture ->
            newerTipRefusesOldPair app witness capture.Facts
            let fresh = captureRegisteredWal capture
            registered <- Some fresh
            let bases = capture.Objects |> List.filter (fun item -> item.Kind = "BASE")
            (bases @ fresh.Objects) |> List.map (fromArchive capture))
        (fun capture verified ->
            let fresh =
                registered
                |> Option.defaultWith (fun () -> failtest "Registered WAL was not captured")

            callback capture fresh verified)
        owner
        app
        writer
        witness

let internal withVerifiedRegisteredCopiesUsingSigner
    callback
    owner
    app
    writer
    (witness: WitnessProtocol)
    =
    withCapturedPrimary
        (fun capture ->
            newerTipRefusesOldPair app witness capture.Facts
            let fresh = captureRegisteredWal capture
            let bases = capture.Objects |> List.filter (fun item -> item.Kind = "BASE")
            let selected = (bases @ fresh.Objects) |> List.map (fromArchive capture)

            verifySelected
                (fun _ -> selected)
                (fun _ _ _ _ _ -> ())
                (fun session verified ->
                    callback capture fresh verified session.CopyKey session.Algorithm)
                owner
                app
                writer
                witness
                capture
            |> ignore)
        owner
        app
        writer
        witness

let private run owner app writer witness =
    withVerifiedCopies
        (fun capture -> [ primaryBase capture ])
        (fun _ verified -> Expect.equal verified.Length 1 "One copy is verified.")
        owner
        app
        writer
        witness

let private runAll owner app writer witness =
    withVerifiedRegisteredCopies
        (fun _ _ verified ->
            Expect.isTrue (verified.Length >= 4) "Both clusters retain BASE and WAL."

            Expect.equal
                (verified
                 |> List.map (fun copy -> copy.Cluster, copy.Kind)
                 |> List.distinct
                 |> List.length)
                4
                "Verified copies cover PRIMARY and WITNESS BASE/WAL.")
        owner
        app
        writer
        witness

let private runCommitHealthInterleaving owner app writer witness =
    withCapturedPrimary
        (fun capture ->
            verifySelected
                (fun value -> [ primaryBase value ])
                (fun session original registration proof _ ->
                    BackupHealthCommitInterleaving.verify
                        session.Owner
                        session.Witness
                        original.CopyId
                        registration
                        proof
                        session.CopyKey
                        session.Algorithm)
                (fun _ _ -> ())
                owner
                app
                writer
                witness
                capture
            |> ignore)
        owner
        app
        writer
        witness

let tests =
    testList
        "owner physical copy process"
        [
            testCase
                "[CC-BACKUP-001] real signed BASE proof drives owner verify-managed-copy and full audit"
                (fun _ -> withAuthorityRuntimeDatabase run)
            testCase
                "[CC-BACKUP-001] both-cluster BASE and WAL copies receive physical verification"
                (fun _ -> withAuthorityRuntimeDatabase runAll)
            testCase
                "[CC-BACKUP-001] retained-copy transition wins authority lock before actor commit health"
                (fun _ -> withAuthorityRuntimeDatabase runCommitHealthInterleaving)
        ]
