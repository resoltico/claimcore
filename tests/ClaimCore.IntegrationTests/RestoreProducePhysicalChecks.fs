module ClaimCore.IntegrationTests.RestoreProducePhysicalChecks

open System.Threading
open System
open System.IO
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests
open ClaimCore.TestSupport

let private reportedPair (facts: RestoredPairFacts) =
    let template, templateIndex, templatePublication = specimen ()

    let report =
        { template with
            InstallationId = facts.InstallationId
            LineageId = facts.LineageId
            Epoch = facts.Epoch
            AuthorityRevision = facts.AuthorityRevision
            PrimarySystemId = facts.PrimarySystemId
            PrimaryTimeline = facts.PrimaryTimeline
            WitnessSystemId = facts.WitnessSystemId
            WitnessTimeline = facts.WitnessTimeline
            CatalogManifestSha256 = facts.CatalogManifestSha256
            WitnessCutoff = facts.WitnessCutoff
            WitnessCutoffHash = facts.WitnessCutoffHash
        }

    let index =
        { templateIndex with
            InstallationId = facts.InstallationId
            LineageId = facts.LineageId
            Epoch = facts.Epoch
            PrimaryWalEndpoint = facts.PrimaryWalEndpoint
            WitnessWalEndpoint = facts.WitnessWalEndpoint
        }

    let publication =
        { templatePublication with
            InstallationId = facts.InstallationId
            LineageId = facts.LineageId
            Epoch = facts.Epoch
            WriterGeneration = facts.WriterGeneration
            WitnessCutoff = facts.WitnessCutoff
            WitnessCutoffHash = facts.WitnessCutoffHash
        }

    report, index, publication

let internal newerTipRefusesOldPair app (witness: WitnessProtocol) (facts: RestoredPairFacts) =
    let principal = human "physical-restore-owner"
    use source = RuntimeDataSource.create app
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        principal,
        actorId grants principal,
        {
            Role = Role.CaseReader
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

    let newer = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.isGreaterThan
        newer.TipSequence
        facts.WitnessCutoff
        "A newer witnessed grant is missing from the old restored pair"

    let report, index, publication = reportedPair facts

    Expect.isTrue
        (DatabaseRestoreLive.matchesLivePair
            publication
            report
            index
            facts
            report.VerifierBinarySha256)
        "The old pair matches its own synthetic cutoff before the counterexample"

    let newerFacts =
        { facts with
            WitnessCutoff = newer.TipSequence
            WitnessCutoffHash = Convert.ToHexStringLower(newer.TipHash)
        }

    Expect.isFalse
        (DatabaseRestoreLive.matchesLivePair
            publication
            report
            index
            newerFacts
            report.VerifierBinarySha256)
        "A newer surviving witness tip quarantines the old restored pair"

let internal physicalCopyProof
    (run: string -> string list -> int * string * string)
    (scratch: string)
    (owner: string)
    (facts: RestoredPairFacts)
    =
    let source = NpgsqlConnectionStringBuilder(owner)

    let role =
        source.Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic owner role is unavailable")

    let database =
        source.Database
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic database name is unavailable")

    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Test-VerifyManagedCopy.py")

    let code, result, stage =
        run
            "python3"
            [
                "-B"
                script
                scratch
                facts.InstallationId.ToString("D")
                facts.LineageId.ToString("D")
                string facts.Epoch
                string facts.WitnessCutoff
                facts.WitnessCutoffHash
                role
                database
            ]

    Expect.equal
        code
        0
        ("Fixed physical copy verifier accepted isolated BASE/WAL proofs at " + stage)

    Expect.equal
        result
        "managed-copy-physical-verifier=base-wal-and-negatives"
        "Only bounded synthetic physical-copy proof status is emitted"

    for kind in [ "base"; "wal" ] do
        let proof = Path.Combine(scratch, "copy-proofs", kind + ".json")
        let tampered = Path.Combine(scratch, "copy-proofs", kind + "-tampered.json")

        Expect.isSome
            (ManagedCopyPhysicalProofCodec.parse (File.ReadAllBytes(proof)))
            "The actual detached signed per-copy bytes satisfy the strict owner proof parser"

        Expect.isNone
            (ManagedCopyPhysicalProofCodec.parse (File.ReadAllBytes(tampered)))
            "A forged readiness flag is not a valid per-copy proof"
