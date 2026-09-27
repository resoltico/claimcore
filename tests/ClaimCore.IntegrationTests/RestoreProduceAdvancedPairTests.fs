module ClaimCore.IntegrationTests.RestoreProduceAdvancedPairTests

open System
open Expecto
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestoreRegisteredWalCapture
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalReplayFixture
open ClaimCore.IntegrationTests.RestoreProducePhysicalChecks

let private walPosition (value: string) =
    let parts = value.Split('/')
    (Convert.ToUInt64(parts[0], 16) <<< 32) ||| Convert.ToUInt64(parts[1], 16)

let private advancedPair owner app writer witness =
    withCapturedPrimary
        (fun capture ->
            newerTipRefusesOldPair app witness capture.Facts
            let registered = captureRegisteredWal capture
            Expect.isTrue (registered.Objects.Length >= 2) "Both source WAL streams were encrypted"

            let witnessBase =
                capture.Objects
                |> List.find (fun item -> item.Cluster = "WITNESS" && item.Kind = "BASE")

            Expect.isTrue
                (walPosition registered.PrimaryHorizon > walPosition capture.WalEndLsn)
                "Primary registered WAL horizon follows the captured BASE end"

            Expect.isTrue
                (walPosition registered.WitnessHorizon > walPosition witnessBase.WalEndLsn.Value)
                "Witness registered WAL horizon follows the captured BASE end"

            let current = witness.Snapshot()

            withAdvancedPair capture owner app writer witness (fun facts _ _ ->
                Expect.equal
                    facts.WitnessCutoff
                    current.TipSequence
                    "Later WAL replay reached current witness tip"

                Expect.equal
                    facts.WitnessCutoffHash
                    (System.Convert.ToHexStringLower current.TipHash)
                    "Later WAL replay retained exact witness hash"

                Expect.equal
                    facts.PrimarySystemId
                    capture.Facts.PrimarySystemId
                    "Primary restored the original BASE lineage"

                Expect.equal
                    facts.WitnessSystemId
                    capture.Facts.WitnessSystemId
                    "Witness restored the original BASE lineage"

                Expect.equal
                    facts.PrimaryTimeline
                    capture.Facts.PrimaryTimeline
                    "Primary WAL stayed on one timeline"

                Expect.equal
                    facts.WitnessTimeline
                    capture.Facts.WitnessTimeline
                    "Witness WAL stayed on one timeline"))
        owner
        app
        writer
        witness

let tests =
    testList
        "advanced physical restored pair"
        [
            testCase
                "[CC-BACKUP-001] original BASE pair replays later synthetic WAL before audit"
                (fun _ -> withAuthorityRuntimeDatabase advancedPair)
        ]
