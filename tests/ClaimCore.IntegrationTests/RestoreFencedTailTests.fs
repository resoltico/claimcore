module ClaimCore.IntegrationTests.RestoreFencedTailTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (items: SortedDictionary<string, objnull>) name value = items.Add(name, box value)

let private canonical (items: SortedDictionary<string, objnull>) =
    Array.append (JsonSerializer.SerializeToUtf8Bytes(items)) [| byte '\n' |]

let private stamp (value: DateTimeOffset) =
    value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

let private digest value = String(value, 64)

let private objectEntry (cluster: string) segment suffix =
    let entry = fields ()
    put entry "objectId" (Guid.NewGuid().ToString("D"))
    put entry "cluster" cluster
    put entry "relativePath" (cluster.ToLowerInvariant() + "-tail-" + suffix + ".age")
    put entry "ciphertextSha256" (digest 'a')
    put entry "ciphertextBytes" 16777316L
    put entry "walSegment" segment
    put entry "walSegmentBytes" 16777216
    entry

let private tailObjects () =
    [|
        for cluster in [ "PRIMARY"; "WITNESS" ] do
            yield objectEntry cluster "000000010000000000000000" "first"
            yield objectEntry cluster "000000010000000000000001" "second"
    |]

let private tailFields (report: RestoreReportClaims) fenceSha now =
    let values = fields ()
    put values "format" "claimcore-fenced-recovery-tail-1"
    put values "scope" "synthetic-only"
    put values "realDataReady" false
    put values "recoveryTailSealed" true
    put values "installationId" (report.InstallationId.ToString("D"))
    put values "lineageId" (report.LineageId.ToString("D"))
    put values "epoch" report.Epoch
    put values "oldGeneration" 2L
    put values "newGeneration" 3L
    put values "handoffId" (Guid.NewGuid().ToString("D"))
    put values "w1Sequence" (report.WitnessCutoff + 5L)
    put values "w1Hash" (digest 'd')
    put values "reportSha256" (digest 'b')
    put values "fenceReportSha256" fenceSha
    put values "checkpointSignerKeyId" (Guid.NewGuid().ToString("D"))
    put values "primaryRegisteredWalHorizon" report.PrimaryRegisteredWalHorizon
    put values "witnessRegisteredWalHorizon" report.WitnessRegisteredWalHorizon
    put values "primaryFinalWalEndpoint" "0/1000001"
    put values "witnessFinalWalEndpoint" "0/1000001"
    put values "archiveRoot" "/synthetic/fenced-tail"
    put values "walObjects" (tailObjects ())
    put values "checkedAt" (stamp now)
    put values "validUntil" (stamp (now.AddMinutes(10.)))
    values

let private fenceFields (report: RestoreReportClaims) now =
    let values = fields ()
    put values "format" "claimcore-old-writer-isolation-1"
    put values "installationId" (report.InstallationId.ToString("D"))
    put values "lineageId" (report.LineageId.ToString("D"))
    put values "epoch" report.Epoch
    put values "oldGeneration" 2L
    put values "newGeneration" 3L
    put values "reportSha256" (digest 'b')
    put values "independentProbeSha256" (digest 'c')
    put values "oldEndpointId" (Guid.NewGuid().ToString("D"))
    put values "oldEndpointAddressSha256" (digest '1')
    put values "oldPrimaryRoleOid" 101L
    put values "oldWitnessRoleOid" 202L
    put values "oldPrimaryCredentialSha256" (digest '2')
    put values "oldWitnessCredentialSha256" (digest '3')
    put values "primarySessionSetSha256" (digest '4')
    put values "witnessSessionSetSha256" (digest '5')
    put values "checkpointSignerKeyId" (Guid.NewGuid().ToString("D"))

    for name in
        [
            "oldWriterStopped"
            "primarySessionsTerminated"
            "witnessSessionsTerminated"
            "oldEndpointIsolated"
            "primaryCredentialRevoked"
            "witnessCredentialRevoked"
            "preIsolationCommitReconciled"
        ] do
        put values name true

    put values "checkedAt" (stamp now)
    put values "validUntil" (stamp (now.AddMinutes(10.)))
    values

let private signedShape =
    testCase
        "[CC-BACKUP-001] W1 supplement and independent fence require exact canonical signed shapes"
        (fun _ ->
            let report, _, _ = specimen ()

            let now =
                DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())

            let fence = fenceFields report now
            let fenceBytes = canonical fence
            let fenceSha = SHA256.HashData(fenceBytes) |> Convert.ToHexStringLower
            let tail = tailFields report fenceSha now
            let tailBytes = canonical tail
            use key = Key.Create(SignatureAlgorithm.Ed25519)
            let publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey)
            let signature = SignatureAlgorithm.Ed25519.Sign(key, tailBytes)

            Expect.isSome
                (DatabaseRestoreWriterFenceClaims.parse fenceBytes now)
                "Exact old-host fence shape is admitted as an attestation"

            Expect.isSome
                (DatabaseRestoreFencedTailClaims.parse tailBytes now)
                "Exact W1 supplement shape is admitted"

            Expect.isTrue
                (ClaimCore.Postgres.ManagedCopySignature.verify publicKey tailBytes signature)
                "Detached Ed25519 signature binds supplement bytes"

            tail["realDataReady"] <- box true

            Expect.isNone
                (DatabaseRestoreFencedTailClaims.parse (canonical tail) now)
                "A signed tail cannot claim real-data readiness"

            tail["realDataReady"] <- box false
            fence["oldEndpointIsolated"] <- box false

            Expect.isNone
                (DatabaseRestoreWriterFenceClaims.parse (canonical fence) now)
                "An accessible old endpoint cannot be called fenced"

            fence["oldEndpointIsolated"] <- box true
            fence["oldPrimaryRoleOid"] <- box 0L

            Expect.isNone
                (DatabaseRestoreWriterFenceClaims.parse (canonical fence) now)
                "An unbound old writer role cannot be called fenced"

            fence["oldPrimaryRoleOid"] <- box 101L
            fence.Remove("primarySessionSetSha256") |> ignore

            Expect.isNone
                (DatabaseRestoreWriterFenceClaims.parse (canonical fence) now)
                "An unbound old session set cannot be called fenced"

            let forged = Array.copy tailBytes
            forged[forged.Length - 2] <- forged[forged.Length - 2] ^^^ 1uy

            Expect.isFalse
                (ClaimCore.Postgres.ManagedCopySignature.verify publicKey forged signature)
                "Changed supplement bytes cannot reuse a detached signature")

let private fixedObjects: FencedWalObject list =
    [
        {
            ObjectId = Guid.Parse("00000000-0000-0000-0000-000000000001")
            Cluster = "PRIMARY"
            RelativePath = "primary-tail/000000010000000000000001.age"
            CiphertextSha256 = String('a', 64)
            CiphertextBytes = 16777316L
            Segment = "000000010000000000000001"
            SegmentBytes = 16777216
        }
        {
            ObjectId = Guid.Parse("00000000-0000-0000-0000-000000000002")
            Cluster = "WITNESS"
            RelativePath = "witness-tail/000000010000000000000001.age"
            CiphertextSha256 = String('b', 64)
            CiphertextBytes = 16777316L
            Segment = "000000010000000000000001"
            SegmentBytes = 16777216
        }
    ]

let private exactCoverage =
    testCase "[CC-BACKUP-001] W1 tail refuses a missing segment or changed timeline" (fun _ ->
        let report, _, _ = specimen ()

        let segments =
            [
                for cluster in [ "PRIMARY"; "WITNESS" ] do
                    yield cluster, "000000010000000000000000", 16777216
                    yield cluster, "000000010000000000000001", 16777216
            ]

        DatabaseRestoreWalCoverage.verifyFencedTail report "0/1000001" "0/1000001" segments

        let missing =
            segments
            |> List.filter (fun (cluster, name, _) ->
                cluster <> "WITNESS" || name <> "000000010000000000000001")

        Expect.throws
            (fun () ->
                DatabaseRestoreWalCoverage.verifyFencedTail report "0/1000001" "0/1000001" missing)
            "A gap in the post-W1 tail refuses"

        let wrongTimeline =
            segments
            |> List.map (fun (cluster, name, bytes) ->
                if cluster = "PRIMARY" then
                    cluster, "00000002" + name.Substring(8), bytes
                else
                    cluster, name, bytes)

        Expect.throws
            (fun () ->
                DatabaseRestoreWalCoverage.verifyFencedTail
                    report
                    "0/1000001"
                    "0/1000001"
                    wrongTimeline)
            "A different primary timeline cannot close the tail"

        Expect.equal
            (DatabaseRestoreWalObjectDigest.compute fixedObjects)
            "b8d0600cad55a63b7e61cf779d95b56f7687abf50fd1172034b355e29358ee35"
            "Canonical final WAL metadata digest has a cross-language fixed vector")

let tests = testList "fenced recovery tail" [ signedShape; exactCoverage ]
