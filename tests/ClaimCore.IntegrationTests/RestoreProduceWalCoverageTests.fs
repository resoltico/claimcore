module ClaimCore.IntegrationTests.RestoreProduceWalCoverageTests

open System
open Expecto
open ClaimCore.Database
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private wal cluster segment bytes suffix =
    {
        ObjectId = Guid.NewGuid()
        CopyId = Guid.NewGuid()
        Cluster = cluster
        Kind = "WAL"
        RelativePath = cluster.ToLowerInvariant() + "-" + suffix + ".age"
        Sha256 = String('a', 64)
        Bytes = 100L
        WalSegment = Some segment
        WalSegmentBytes = Some bytes
    }

let private ordinary =
    testCase "[CC-BACKUP-001] WAL coverage refuses missing duplicate and changed timeline" (fun _ ->
        let report, index, _ = specimen ()
        DatabaseRestoreWalCoverage.verify index report

        let missing =
            { index with
                PrimaryRegisteredWalHorizon = "0/1000001"
                PrimaryWalEndpoint = "0/1000002"
            }

        Expect.throws
            (fun () -> DatabaseRestoreWalCoverage.verify missing report)
            "A missing next WAL segment cannot cover the audited endpoint"

        let first =
            index.ArchiveObjects
            |> List.find (fun item -> item.Cluster = "PRIMARY" && item.Kind = "WAL")

        let repeated =
            { first with
                ObjectId = Guid.NewGuid()
                CopyId = Guid.NewGuid()
                RelativePath = "repeat.age"
            }

        let duplicate =
            { index with
                ArchiveObjects = repeated :: index.ArchiveObjects
            }

        Expect.throws
            (fun () -> DatabaseRestoreWalCoverage.verify duplicate report)
            "A duplicated segment does not constitute additional coverage"

        let wrong =
            { first with
                WalSegment = Some "000000020000000000000000"
            }

        let changed =
            { index with
                ArchiveObjects =
                    wrong
                    :: (index.ArchiveObjects
                        |> List.filter (fun item -> item.ObjectId <> first.ObjectId))
            }

        Expect.throws
            (fun () -> DatabaseRestoreWalCoverage.verify changed report)
            "WAL from another timeline cannot qualify the restored pair")

let private logBoundary =
    testCase "[CC-BACKUP-001] WAL segment rollover is contiguous only on one timeline" (fun _ ->
        let report, index, _ = specimen ()

        let baseObjects =
            index.ArchiveObjects |> List.filter (fun item -> item.Kind = "BASE")

        let before = "000000010000000000000FFF"
        let after = "000000010000000100000000"

        let objects =
            baseObjects
            @ [
                wal "PRIMARY" before 1048576 "before"
                wal "PRIMARY" after 1048576 "after"
                wal "WITNESS" before 1048576 "before"
                wal "WITNESS" after 1048576 "after"
            ]

        let covering =
            { index with
                ArchiveObjects = objects
                PrimaryCaptureWalEndpoint = "0/FFFFF000"
                WitnessCaptureWalEndpoint = "0/FFFFF000"
                PrimaryRegisteredWalHorizon = "1/1000"
                WitnessRegisteredWalHorizon = "1/1000"
                PrimaryWalEndpoint = "1/2000"
                WitnessWalEndpoint = "1/2000"
            }

        DatabaseRestoreWalCoverage.verify covering report

        let missingMiddle =
            { covering with
                ArchiveObjects =
                    objects
                    |> List.filter (fun item ->
                        item.Cluster <> "PRIMARY" || item.WalSegment <> Some after)
            }

        Expect.throws
            (fun () -> DatabaseRestoreWalCoverage.verify missingMiddle report)
            "A missing segment after 4 GiB rollover refuses")

let tests = testList "restore WAL coverage" [ ordinary; logBoundary ]
