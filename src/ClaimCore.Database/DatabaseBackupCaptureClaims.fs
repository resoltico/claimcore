namespace ClaimCore.Database

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

[<NoEquality; NoComparison>]
type internal BackupCaptureClaims =
    {
        CycleId: Guid
        CopySigningKeyId: Guid
        CheckpointSigningKeyId: Guid
    }

/// Signed producer fields must bind the owner-held authority cutoff and every reopened byte.
/// This remains CAPTURED_UNVERIFIED: no field here asserts a retained or test-restored copy.
module internal DatabaseBackupCaptureClaims =
    let private exact names value =
        if not (DatabaseRestoreCanonical.exactProperties names value) then
            invalidOp "Backup capture signed field set changed."

    let private value name (root: JsonElement) =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Backup capture signed text is absent.")

    let private same name root expected =
        if value name root <> expected then
            invalidOp "Backup capture signed identity differs."

    let private number name root expected =
        if DatabaseRestoreCanonical.number name root <> expected then
            invalidOp "Backup capture signed number differs."

    let private id name root =
        let raw = value name root

        match Guid.TryParseExact(raw, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = raw -> parsed
        | _ -> invalidOp "Backup capture signed identifier is invalid."

    let private sha name root =
        let raw = value name root

        if
            raw.Length <> 64
            || raw <> raw.ToLowerInvariant()
            || (raw
                |> Seq.exists (fun c -> not (('0' <= c && c <= '9') || ('a' <= c && c <= 'f'))))
        then
            invalidOp "Backup capture signed digest is invalid."

        raw

    let private instant name root =
        let raw = value name root

        let parsed =
            DateTimeOffset.ParseExact(
                raw,
                "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if parsed.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw then
            invalidOp "Backup capture signed instant is invalid."

        parsed

    let private cutoff (held: BackupCaptureHeld) (root: JsonElement) =
        same "leaseId" root (held.LeaseId.ToString("D"))
        same "captureNonce" root held.Nonce
        number "writerGeneration" root held.Cutoff.WriterGeneration
        number "backupCaptureSequence" root held.Cutoff.WitnessSequence
        same "backupCaptureHash" root (Convert.ToHexStringLower held.Cutoff.WitnessHash)
        same "maintenanceEvidenceSha256" root held.MaintenanceEvidenceSha256

    let private identity (held: BackupCaptureHeld) (root: JsonElement) (cycle: Guid) =
        same "cycleId" root (cycle.ToString("D"))
        same "installationId" root (held.Cutoff.InstallationId.ToString("D"))
        same "lineageId" root (held.Cutoff.LineageId.ToString("D"))
        number "epoch" root held.Cutoff.Epoch
        cutoff held root

    let private metadata (held: BackupCaptureHeld) cluster (root: JsonElement) =
        exact [ "before"; "after"; "ciphertextSha256"; "ciphertextBytes" ] root
        let expectedLength = if cluster = "witness" then 5 else 3

        for name in [ "before"; "after" ] do
            let fields = root.GetProperty(name)

            if
                fields.ValueKind <> JsonValueKind.Array
                || fields.GetArrayLength() <> expectedLength
            then
                invalidOp "Backup capture cluster metadata is invalid."

            let expected =
                [|
                    held.Cutoff.InstallationId.ToString("D")
                    held.Cutoff.LineageId.ToString("D")
                    held.Cutoff.Epoch.ToString(CultureInfo.InvariantCulture)
                |]

            for index in 0..2 do
                if fields[index].GetString() <> expected[index] then
                    invalidOp "Backup capture cluster identity differs."

            if cluster = "witness" then
                if
                    fields[3].GetString()
                    <> held.Cutoff.WitnessSequence.ToString(CultureInfo.InvariantCulture)
                    || fields[4].GetString() <> Convert.ToHexStringLower held.Cutoff.WitnessHash
                then
                    invalidOp "Backup capture witness tip differs."

    let private baseCopy
        (held: BackupCaptureHeld)
        cluster
        (details: JsonElement)
        (copyIds: JsonElement)
        (root: JsonElement)
        (digest: BackupCaptureFileDigest)
        =
        exact
            [
                "copyId"
                "eventId"
                "postgresSystemId"
                "timeline"
                "walSegmentBytes"
                "backupManifestSha256"
                "walStartLsn"
                "walEndLsn"
                "ciphertextSha256"
                "ciphertextBytes"
                "locationCommitment"
                "custodianCommitment"
            ]
            root

        let expectedSystem, expectedTimeline =
            if cluster = "primary" then
                held.PrimarySystemId, held.PrimaryTimeline
            else
                held.WitnessSystemId, held.WitnessTimeline

        same "postgresSystemId" root expectedSystem
        number "timeline" root expectedTimeline

        let segment = DatabaseRestoreCanonical.number "walSegmentBytes" root

        if segment < 1048576L || segment > 1073741824L || segment &&& (segment - 1L) <> 0L then
            invalidOp "Backup capture WAL segment size is invalid."

        id "eventId" root |> ignore

        if id "copyId" root <> id cluster copyIds then
            invalidOp "Backup capture copy identity differs."

        for field in [ "backupManifestSha256"; "locationCommitment"; "custodianCommitment" ] do
            sha field root |> ignore

        for field in [ "walStartLsn"; "walEndLsn" ] do
            let lsn = value field root

            if not (Regex.IsMatch(lsn, "^[0-9A-F]{1,8}/[0-9A-F]{1,8}$")) then
                invalidOp "Backup capture WAL range is invalid."

        same "ciphertextSha256" root digest.Sha256
        number "ciphertextBytes" root digest.Bytes
        same "ciphertextSha256" details digest.Sha256
        number "ciphertextBytes" details digest.Bytes

    let private shape (manifest: JsonElement) (checkpoint: JsonElement) =
        exact
            [
                "format"
                "cycleId"
                "consistencyScope"
                "capturedAt"
                "installationId"
                "lineageId"
                "epoch"
                "primary"
                "witness"
                "witnessCheckpoint"
                "copyIds"
                "baseCopies"
                "copySigningKeyId"
                "checkpointSigningKeyId"
                "checkpointSha256"
                "leaseId"
                "captureNonce"
                "writerGeneration"
                "backupCaptureSequence"
                "backupCaptureHash"
                "maintenanceEvidenceSha256"
            ]
            manifest

        exact
            [
                "format"
                "cycleId"
                "installationId"
                "lineageId"
                "epoch"
                "sequence"
                "hash"
                "capturedAt"
                "checkpointSigningKeyId"
                "checkpointCustodianCommitment"
                "leaseId"
                "captureNonce"
                "writerGeneration"
                "backupCaptureSequence"
                "backupCaptureHash"
                "maintenanceEvidenceSha256"
            ]
            checkpoint

    let private sharedIdentity
        (held: BackupCaptureHeld)
        (files: BackupCaptureFileDigests)
        (manifest: JsonElement)
        (checkpoint: JsonElement)
        =
        same "format" manifest "claimcore-backup-cycle-1"
        same "consistencyScope" manifest "unfenced-capture"
        same "format" checkpoint "claimcore-witness-checkpoint-1"
        let cycle = id "cycleId" manifest
        identity held manifest cycle
        identity held checkpoint cycle
        let captured = instant "capturedAt" manifest

        if
            captured < held.CheckedAt.AddSeconds(-1.)
            || captured >= held.ValidUntil
            || instant "capturedAt" checkpoint <> captured
        then
            invalidOp "Backup capture time leaves the owner lease."

        let signing = id "copySigningKeyId" manifest
        let checkpointSigning = id "checkpointSigningKeyId" manifest

        if
            signing = checkpointSigning
            || id "checkpointSigningKeyId" checkpoint <> checkpointSigning
        then
            invalidOp "Backup capture signer custody overlaps."

        sha "checkpointCustodianCommitment" checkpoint |> ignore
        same "checkpointSha256" manifest files.Checkpoint.Sha256
        number "sequence" checkpoint held.Cutoff.WitnessSequence
        same "hash" checkpoint (Convert.ToHexStringLower held.Cutoff.WitnessHash)
        let tip = manifest.GetProperty("witnessCheckpoint")
        exact [ "sequence"; "hash" ] tip
        number "sequence" tip held.Cutoff.WitnessSequence
        same "hash" tip (Convert.ToHexStringLower held.Cutoff.WitnessHash)
        cycle, signing, checkpointSigning

    let verify
        (held: BackupCaptureHeld)
        (files: BackupCaptureFileDigests)
        (manifest: JsonElement)
        (checkpoint: JsonElement)
        =
        shape manifest checkpoint

        let cycle, signing, checkpointSigning =
            sharedIdentity held files manifest checkpoint

        let copyIds = manifest.GetProperty("copyIds")
        let copies = manifest.GetProperty("baseCopies")
        exact [ "primary"; "witness" ] copyIds
        exact [ "primary"; "witness" ] copies

        if id "primary" copyIds = id "witness" copyIds then
            invalidOp "Backup capture copies share an identity."

        for cluster, digest in [ "primary", files.Primary; "witness", files.Witness ] do
            let details = manifest.GetProperty(cluster)
            metadata held cluster details
            baseCopy held cluster details copyIds (copies.GetProperty cluster) digest

        {
            CycleId = cycle
            CopySigningKeyId = signing
            CheckpointSigningKeyId = checkpointSigning
        }
