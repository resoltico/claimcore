module ClaimCore.IntegrationTests.RestoreProduceAdvancedPairTests

open System.Threading
open System
open Expecto
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestoreRegisteredWalCapture
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalReplayFixture
open ClaimCore.IntegrationTests.RestorePhysicalWalRange
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

            let current = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

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
                "[CC-BACKUP-001] completed WAL endpoints retain the exclusive last segment"
                (fun _ ->
                    let completed = "000000010000000000000006"
                    let following = "000000010000000000000007"
                    let segments = requiredSegments "0/5000028" 1 16777216 "0/7000000" completed

                    Expect.equal
                        segments
                        ("000000010000000000000005," + completed)
                        "The exclusive boundary includes the preceding completed segment"

                    Expect.throws
                        (fun () ->
                            requiredSegments "0/5000028" 1 16777216 "0/7000000" following
                            |> ignore)
                        "The following uncompleted segment cannot satisfy the horizon"

                    withAuthorityRuntimeDatabase (fun owner _ _ _ ->
                        for _ in 1..2 do
                            let horizon, last = completedWalEndpoint owner
                            let index = (walPosition horizon - 1UL) / 16777216UL
                            let expected = $"{1:X8}{index / 256UL:X8}{index % 256UL:X8}"

                            Expect.equal
                                last
                                expected
                                "Each switch uses its own exclusive horizon"))
            testCase
                "[CC-BACKUP-001] original BASE pair replays later synthetic WAL before audit"
                (fun _ -> withAuthorityRuntimeDatabase advancedPair)
        ]
