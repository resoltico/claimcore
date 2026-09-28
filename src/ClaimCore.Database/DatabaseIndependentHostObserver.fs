namespace ClaimCore.Database

open System
open System.Text.Json

/// Sixth independent signer attests the old route, sessions and credentials after W1.
module internal DatabaseIndependentHostObserver =
    let private fields =
        [
            "format"
            "role"
            "nonce"
            "installationId"
            "lineageId"
            "epoch"
            "oldGeneration"
            "newGeneration"
            "w1Sequence"
            "w1Hash"
            "reportSha256"
            "fenceReportSha256"
            "oldEndpointId"
            "oldEndpointAddressSha256"
            "oldPrimaryRoleOid"
            "oldWitnessRoleOid"
            "oldPrimaryCredentialSha256"
            "oldWitnessCredentialSha256"
            "primarySessionSetSha256"
            "witnessSessionSetSha256"
            "routeClosed"
            "primarySessionsZero"
            "witnessSessionsZero"
            "primaryCredentialDenied"
            "witnessCredentialDenied"
            "containerized"
            "machineHash"
            "storageHash"
            "adminActorId"
            "hostKeyId"
            "checkedAt"
            "validUntil"
        ]

    let private named (pin: IndependentFencePin) (fence: WriterFenceClaims) report =
        [
            "oldEndpointAddressSha256", pin.OldEndpointAddressSha256, fence.OldEndpointAddressSha256
            "oldPrimaryCredentialSha256",
            pin.OldPrimaryCredentialSha256,
            fence.OldPrimaryCredentialSha256
            "oldWitnessCredentialSha256",
            pin.OldWitnessCredentialSha256,
            fence.OldWitnessCredentialSha256
            "primarySessionSetSha256", pin.PrimarySessionSetSha256, fence.PrimarySessionSetSha256
            "witnessSessionSetSha256", pin.WitnessSessionSetSha256, fence.WitnessSessionSetSha256
        ]
        |> List.forall (fun (name, expected, inFence) ->
            DatabaseIndependentHostJson.digest name report = expected && expected = inFence)

    let private sameAuthority report nonce reportSha fenceSha (tail: FencedTailClaims) =
        DatabaseIndependentHostJson.text "format" report =
            "claimcore-old-writer-fence-observation-1"
        && DatabaseIndependentHostJson.text "nonce" report = nonce
        && DatabaseIndependentHostJson.uuid "installationId" report = tail.InstallationId
        && DatabaseIndependentHostJson.uuid "lineageId" report = tail.LineageId
        && DatabaseIndependentHostJson.integer "epoch" report = tail.Epoch
        && DatabaseIndependentHostJson.integer "oldGeneration" report = tail.OldGeneration
        && DatabaseIndependentHostJson.integer "newGeneration" report = tail.NewGeneration
        && DatabaseIndependentHostJson.integer "w1Sequence" report = tail.W1Sequence
        && DatabaseIndependentHostJson.digest "w1Hash" report = tail.W1Hash
        && DatabaseIndependentHostJson.digest "reportSha256" report = reportSha
        && DatabaseIndependentHostJson.digest "fenceReportSha256" report = fenceSha

    let private sameOldWriter report (pin: IndependentFencePin) (fence: WriterFenceClaims) =
        DatabaseIndependentHostJson.uuid "oldEndpointId" report = pin.OldEndpointId
        && pin.OldEndpointId = fence.OldEndpointId
        && DatabaseIndependentHostJson.integer "oldPrimaryRoleOid" report = pin.OldPrimaryRoleOid
        && pin.OldPrimaryRoleOid = fence.OldPrimaryRoleOid
        && DatabaseIndependentHostJson.integer "oldWitnessRoleOid" report = pin.OldWitnessRoleOid
        && pin.OldWitnessRoleOid = fence.OldWitnessRoleOid
        && named pin fence report

    let private sameHost report (pin: IndependentFencePin) =
        DatabaseIndependentHostJson.digest "machineHash" report = pin.Role.MachineHash
        && DatabaseIndependentHostJson.digest "storageHash" report = pin.Role.StorageHash
        && DatabaseIndependentHostJson.uuid "adminActorId" report = pin.Role.AdminActorId
        && DatabaseIndependentHostJson.uuid "hostKeyId" report = pin.Role.HostKeyId

    let private independentFence report =
        not (DatabaseIndependentHostJson.flag "containerized" report)
        && ([
                "routeClosed"
                "primarySessionsZero"
                "witnessSessionsZero"
                "primaryCredentialDenied"
                "witnessCredentialDenied"
            ]
            |> List.forall (fun name -> DatabaseIndependentHostJson.flag name report))

    let private fresh
        (checkedAt: DateTimeOffset)
        (expires: DateTimeOffset)
        (aggregateCheckedAt: DateTimeOffset)
        (now: DateTimeOffset option)
        =
        checkedAt <= aggregateCheckedAt
        && aggregateCheckedAt < expires
        && expires <= checkedAt.AddSeconds(90.)
        && (match now with
            | Some current -> abs (current - checkedAt).TotalSeconds <= 30. && current < expires
            | None -> abs (aggregateCheckedAt - checkedAt).TotalSeconds <= 30.)

    let verify
        (entry: JsonElement)
        (pin: IndependentFencePin)
        (publicKeyPem: byte array)
        nonce
        reportSha
        fenceSha
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        (aggregateCheckedAt: DateTimeOffset)
        (now: DateTimeOffset option)
        =
        if DatabaseIndependentHostJson.sha256 publicKeyPem <> pin.Role.PublicKeySha256 then
            invalidOp "Old-writer observer key differs from root-signed topology."

        let observed =
            DatabaseIndependentHostRaw.verify
                entry
                "observationSha256"
                publicKeyPem
                "old-writer-fence"

        DatabaseIndependentHostRaw.withReport observed (fun report ->
            DatabaseIndependentHostJson.exact fields report
            let observedAt = DatabaseIndependentHostJson.instant "checkedAt" report
            let expires = DatabaseIndependentHostJson.instant "validUntil" report

            if
                not (sameAuthority report nonce reportSha fenceSha tail)
                || not (sameOldWriter report pin fence)
                || not (sameHost report pin)
                || observed.CheckedAt <> observedAt
                || observed.ValidUntil <> expires
                || not (independentFence report)
                || not (fresh observedAt expires aggregateCheckedAt now)
            then
                invalidOp "Old-writer observer is invalid or stale.")

        observed.Digest, observed.ValidUntil
