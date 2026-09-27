namespace ClaimCore.Database

open System
open System.Text.Json

/// Exact signed role probes, including archive possession of every post-W1 encrypted WAL object.
module internal DatabaseIndependentHostChildren =
    let private common =
        [
            "format"
            "role"
            "nonce"
            "qualificationSha256"
            "issuedAt"
            "expiresAt"
            "machineHash"
            "storageHash"
            "adminActorId"
            "hostKeyId"
            "containerized"
            "availabilityKind"
            "available"
            "keyChallengeProofSha256"
            "installationId"
            "lineageId"
            "epoch"
        ]

    let private database =
        [ "postgresSystemId"; "timeline"; "witnessTipSequence"; "witnessTipHash" ]

    let private copy = [ "objectId"; "objectSha256"; "objectBytes" ]

    let private archive =
        [ "finalWalObjects"; "finalWalObjectCount"; "finalWalObjectSha256" ]

    let private key =
        [ "custodyKeyId"; "custodyPublicKeySha256"; "softwareKeyExportable" ]

    let private fields role =
        match role with
        | "primary"
        | "witness" -> common @ database
        | "archive" -> common @ copy @ archive
        | "checkpoint" -> common @ copy
        | "key" -> common @ key
        | _ -> invalidOp "Independent host role is invalid."

    let private availability role =
        match role with
        | "primary"
        | "witness" -> "database-read"
        | "archive"
        | "checkpoint" -> "retained-copy"
        | "key" -> "key-possession"
        | _ -> invalidOp "Independent host role is invalid."

    let private nullKeyProof role (report: JsonElement) =
        let value = report.GetProperty("keyChallengeProofSha256")

        if role = "key" then
            value.ValueKind = JsonValueKind.String
            && DatabaseIndependentHostJson.digest "keyChallengeProofSha256" report
               <> String.replicate 64 "0"
        else
            value.ValueKind = JsonValueKind.Null

    let private sameObject (value: JsonElement) (expected: RestoreCustodyObject) =
        DatabaseIndependentHostJson.uuid "objectId" value = expected.ObjectId
        && DatabaseIndependentHostJson.digest "objectSha256" value = expected.Sha256
        && DatabaseIndependentHostJson.integer "objectBytes" value = expected.Bytes

    let private sameFinalObject (actual: JsonElement) (expected: FencedWalObject) =
        DatabaseIndependentHostJson.exact
            [
                "objectId"
                "cluster"
                "relativePath"
                "ciphertextSha256"
                "ciphertextBytes"
                "walSegment"
                "walSegmentBytes"
            ]
            actual

        DatabaseIndependentHostJson.uuid "objectId" actual = expected.ObjectId
        && DatabaseIndependentHostJson.text "cluster" actual = expected.Cluster
        && DatabaseIndependentHostJson.text "relativePath" actual = expected.RelativePath
        && DatabaseIndependentHostJson.digest "ciphertextSha256" actual = expected.CiphertextSha256
        && DatabaseIndependentHostJson.integer "ciphertextBytes" actual = expected.CiphertextBytes
        && DatabaseIndependentHostJson.text "walSegment" actual = expected.Segment
        && DatabaseIndependentHostJson.integer "walSegmentBytes" actual =
            int64 expected.SegmentBytes

    let private databaseEvidence
        role
        (report: JsonElement)
        (backup: RestoreReportClaims)
        (tail: FencedTailClaims)
        =
        let systemId, timeline =
            if role = "primary" then
                backup.PrimarySystemId, backup.PrimaryTimeline
            else
                backup.WitnessSystemId, backup.WitnessTimeline

        let tipSequence = report.GetProperty("witnessTipSequence")
        let tipHash = report.GetProperty("witnessTipHash")

        DatabaseIndependentHostJson.text "postgresSystemId" report = systemId
        && DatabaseIndependentHostJson.integer "timeline" report = timeline
        && (if role = "witness" then
                DatabaseIndependentHostJson.integer "witnessTipSequence" report = tail.W1Sequence
                && DatabaseIndependentHostJson.digest "witnessTipHash" report = tail.W1Hash
            else
                tipSequence.ValueKind = JsonValueKind.Null
                && tipHash.ValueKind = JsonValueKind.Null)

    let private copyEvidence
        role
        (report: JsonElement)
        (backup: RestoreReportClaims)
        (tail: FencedTailClaims)
        =
        let expected =
            if role = "archive" then
                backup.ArchiveCustody
            else
                backup.CheckpointCustody

        let baseCopy = sameObject report expected

        if role = "checkpoint" then
            baseCopy
        else
            let items = report.GetProperty("finalWalObjects")

            baseCopy
            && items.ValueKind = JsonValueKind.Array
            && items.GetArrayLength() = tail.WalObjects.Length
            && DatabaseIndependentHostJson.integer "finalWalObjectCount" report =
                int64 tail.WalObjects.Length
            && DatabaseIndependentHostJson.digest "finalWalObjectSha256" report =
                DatabaseRestoreWalObjectDigest.compute tail.WalObjects
            && (items.EnumerateArray(), tail.WalObjects) ||> Seq.forall2 sameFinalObject

    let private roleEvidence role report backup tail =
        match role with
        | "primary"
        | "witness" -> databaseEvidence role report backup tail
        | "archive"
        | "checkpoint" -> copyEvidence role report backup tail
        | "key" ->
            DatabaseIndependentHostJson.uuid "custodyKeyId" report = backup.CustodyKeyId
            && DatabaseIndependentHostJson.digest "custodyPublicKeySha256" report =
                backup.CustodyPublicKeySha256
            && DatabaseIndependentHostJson.flag "softwareKeyExportable" report
        | _ -> false

    let private timely
        (issued: DateTimeOffset)
        (expires: DateTimeOffset)
        (checkedAt: DateTimeOffset)
        (now: DateTimeOffset option)
        =
        issued <= checkedAt
        && checkedAt < expires
        && expires <= issued.AddSeconds(90.)
        && (match now with
            | Some current -> abs (current - issued).TotalSeconds <= 30. && current < expires
            | None -> abs (checkedAt - issued).TotalSeconds <= 30.)

    let private matchesIdentity report nonce supplementSha (tail: FencedTailClaims) =
        DatabaseIndependentHostJson.text "format" report = "claimcore-deployment-probe-1"
        && DatabaseIndependentHostJson.text "nonce" report = nonce
        && DatabaseIndependentHostJson.digest "qualificationSha256" report = supplementSha
        && DatabaseIndependentHostJson.uuid "installationId" report = tail.InstallationId
        && DatabaseIndependentHostJson.uuid "lineageId" report = tail.LineageId
        && DatabaseIndependentHostJson.integer "epoch" report = tail.Epoch

    let private matchesPin report (pin: IndependentRolePin) =
        DatabaseIndependentHostJson.digest "machineHash" report = pin.MachineHash
        && DatabaseIndependentHostJson.digest "storageHash" report = pin.StorageHash
        && DatabaseIndependentHostJson.uuid "adminActorId" report = pin.AdminActorId
        && DatabaseIndependentHostJson.uuid "hostKeyId" report = pin.HostKeyId

    let private matchesStatus report role =
        not (DatabaseIndependentHostJson.flag "containerized" report)
        && DatabaseIndependentHostJson.flag "available" report
        && DatabaseIndependentHostJson.text "availabilityKind" report = availability role
        && nullKeyProof role report

    let verify
        (entry: JsonElement)
        (pin: IndependentRolePin)
        (publicKeyPem: byte array)
        nonce
        supplementSha
        (backup: RestoreReportClaims)
        (tail: FencedTailClaims)
        (checkedAt: DateTimeOffset)
        (now: DateTimeOffset option)
        =
        let role = pin.Role

        if DatabaseIndependentHostJson.sha256 publicKeyPem <> pin.PublicKeySha256 then
            invalidOp "Independent role key differs from root-signed topology."

        let observed =
            DatabaseIndependentHostRaw.verify entry "probeSha256" publicKeyPem role

        DatabaseIndependentHostRaw.withReport observed (fun report ->
            DatabaseIndependentHostJson.exact (fields role) report
            let issued = DatabaseIndependentHostJson.instant "issuedAt" report
            let expires = DatabaseIndependentHostJson.instant "expiresAt" report

            if
                not (matchesIdentity report nonce supplementSha tail)
                || not (matchesPin report pin)
                || observed.CheckedAt <> issued
                || observed.ValidUntil <> expires
                || not (matchesStatus report role)
                || not (timely issued expires checkedAt now)
                || not (roleEvidence role report backup tail)
            then
                invalidOp "Independent role observation is invalid or stale.")

        observed.Digest, observed.ValidUntil
