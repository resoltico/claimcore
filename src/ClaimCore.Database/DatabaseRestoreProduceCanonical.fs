namespace ClaimCore.Database

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json

[<NoEquality; NoComparison>]
type internal RestoreProducedEvidence =
    {
        Report: byte array
        EvidenceIndex: byte array
        ReportSha256: string
        EvidenceIndexSha256: string
    }

/// Emits the exact byte representation accepted by the independent report reader.
/// Values are derived by the producer; this module never treats a caller flag as proof.
module internal DatabaseRestoreProduceCanonical =
    let private fields () =
        SortedDictionary<string, objnull>(StringComparer.Ordinal)

    let private put (items: SortedDictionary<string, objnull>) name value =
        items.Add(name, box value)

    let private bytes (items: SortedDictionary<string, objnull>) =
        let encoded = JsonSerializer.SerializeToUtf8Bytes(items)
        let result = Array.zeroCreate<byte>(encoded.Length + 1)
        encoded.CopyTo(result, 0)
        result[result.Length - 1] <- byte '\n'

        match DatabaseRestoreCanonical.parse result with
        | None -> invalidOp "Produced restore evidence is not canonical."
        | Some document ->
            document.Dispose()
            result

    let private stamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

    let private identity (items: SortedDictionary<string, objnull>) (report: RestoreReportClaims) =
        put items "installationId" (report.InstallationId.ToString("D"))
        put items "lineageId" (report.LineageId.ToString("D"))
        put items "epoch" report.Epoch
        put items "cycleId" (report.CycleId.ToString("D"))

    let private indexPaths
        (items: SortedDictionary<string, objnull>)
        (index: RestoreEvidenceIndex)
        =
        put items "checkpointRoot" index.CheckpointRoot
        put items "checkpointFile" index.CheckpointFile
        put items "checkpointSignatureFile" index.CheckpointSignatureFile
        put items "manifestFile" index.ManifestFile
        put items "manifestSignatureFile" index.ManifestSignatureFile
        put items "manifestSha256" index.ManifestSha256
        put items "barrierFile" index.BarrierFile
        put items "barrierSignatureFile" index.BarrierSignatureFile
        put items "inventoryRoot" index.InventoryRoot
        put items "inventorySnapshotFile" index.InventorySnapshotFile
        put items "inventorySnapshotSignatureFile" index.InventorySnapshotSignatureFile

    let private archiveObject (value: RestoreArchiveObject) =
        let entry = fields ()
        put entry "objectId" (value.ObjectId.ToString("D"))
        put entry "copyId" (value.CopyId.ToString("D"))
        put entry "cluster" value.Cluster
        put entry "kind" value.Kind
        put entry "relativePath" value.RelativePath
        put entry "sha256" value.Sha256
        put entry "bytes" value.Bytes
        put entry "walSegment" (value.WalSegment |> Option.toObj)

        put
            entry
            "walSegmentBytes"
            (value.WalSegmentBytes |> Option.map box |> Option.defaultValue null)

        entry

    let evidenceIndex
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (publication: TrustedRestorePublication)
        =
        let items = fields ()
        identity items report
        put items "format" "claimcore-restore-evidence-index-1"
        put items "backupCaptureSequence" index.BackupCaptureSequence
        put items "backupCaptureHash" index.BackupCaptureHash
        put items "publicationManifestSha256" publication.ManifestSha256
        put items "archiveRoot" index.ArchiveRoot
        put items "archiveSetId" (index.ArchiveSetId.ToString("D"))

        let archiveObjects =
            index.ArchiveObjects
            |> DatabaseRestoreArchiveObjects.validate
            |> List.map archiveObject
            |> List.toArray

        put items "archiveObjects" archiveObjects
        put items "primaryCaptureWalEndpoint" index.PrimaryCaptureWalEndpoint
        put items "witnessCaptureWalEndpoint" index.WitnessCaptureWalEndpoint
        put items "primaryRegisteredWalHorizon" index.PrimaryRegisteredWalHorizon
        put items "witnessRegisteredWalHorizon" index.WitnessRegisteredWalHorizon
        put items "primarySystemId" report.PrimarySystemId
        put items "primaryTimeline" report.PrimaryTimeline
        put items "witnessSystemId" report.WitnessSystemId
        put items "witnessTimeline" report.WitnessTimeline
        put items "primaryWalEndpoint" index.PrimaryWalEndpoint
        put items "witnessWalEndpoint" index.WitnessWalEndpoint
        indexPaths items index
        put items "reportSignerKeyId" (report.SignerKeyId.ToString("D"))
        put items "checkpointSignerKeyId" (index.CheckpointSignerKeyId.ToString("D"))
        put items "checkpointObjectId" (index.CheckpointObjectId.ToString("D"))
        bytes items

    let private custodyObject (id: Guid) digest length =
        let items = fields ()
        put items "objectId" (id.ToString("D"))
        put items "sha256" digest
        put items "bytes" length
        items

    let private reportFacts
        (items: SortedDictionary<string, objnull>)
        (claims: RestoreReportClaims)
        =
        put items "witnessCutoff" claims.WitnessCutoff
        put items "witnessCutoffHash" claims.WitnessCutoffHash
        put items "backupCaptureSequence" claims.BackupCaptureSequence
        put items "backupCaptureHash" claims.BackupCaptureHash
        put items "primarySystemId" claims.PrimarySystemId
        put items "primaryTimeline" claims.PrimaryTimeline
        put items "witnessSystemId" claims.WitnessSystemId
        put items "witnessTimeline" claims.WitnessTimeline
        put items "primaryRegisteredWalHorizon" claims.PrimaryRegisteredWalHorizon
        put items "witnessRegisteredWalHorizon" claims.WitnessRegisteredWalHorizon
        put items "reportSignerKeyId" (claims.SignerKeyId.ToString("D"))
        put items "verifierBinarySha256" claims.VerifierBinarySha256
        put items "evidenceIndexSha256" claims.EvidenceIndexSha256
        put items "checkpointSha256" claims.CheckpointSha256
        put items "signedInventoryFileSha256" claims.SignedInventoryFileSha256
        put items "quiescentBarrierSha256" claims.QuiescentBarrierSha256
        put items "catalogManifestSha256" claims.CatalogManifestSha256
        put items "authorityRevision" claims.AuthorityRevision
        put items "checkedAt" (stamp claims.CheckedAt)
        put items "validUntil" (stamp claims.ValidUntil)

    let private reportChecks
        (items: SortedDictionary<string, objnull>)
        (claims: RestoreReportClaims)
        =
        for name in
            [
                "recoveredDataChecked"
                "pairCompared"
                "catalogVerified"
                "dataAuditVerified"
                "registeredWalVerified"
                "recoveryTailUnsealed"
                "authorityReconciled"
                "managedCopiesRegistered"
                "newerFencesApplied"
                "quiescentAuditBarrierVerified"
                "twoOwnerRosterVerified"
            ] do
            put items name true

        put items "oidcIssuerHttpsVerified" (claims.Scope = "full")
        put items "pendingIntents" 0L

    let private reportCustody
        (items: SortedDictionary<string, objnull>)
        (claims: RestoreReportClaims)
        (custodyKeyId: Guid)
        custodyPublicKeySha256
        =
        let custody = fields ()

        put
            custody
            "archive"
            (custodyObject
                claims.ArchiveCustody.ObjectId
                claims.ArchiveCustody.Sha256
                claims.ArchiveCustody.Bytes)

        put
            custody
            "checkpoint"
            (custodyObject
                claims.CheckpointCustody.ObjectId
                claims.CheckpointCustody.Sha256
                claims.CheckpointCustody.Bytes)

        put items "custodyObjects" custody
        put items "custodyKeyId" (custodyKeyId.ToString("D"))
        put items "custodyPublicKeySha256" custodyPublicKeySha256

    let private reportApprovers
        (items: SortedDictionary<string, objnull>)
        (claims: RestoreReportClaims)
        =
        let approvers =
            claims.AuthorizedApprovers
            |> List.sortBy _.ActorId
            |> List.map (fun value ->
                let entry = fields ()
                put entry "actorId" (value.ActorId.ToString("D"))
                put entry "approvalEventId" (value.ApprovalEventId.ToString("D"))
                put entry "active" true
                put entry "role" "owner"
                put entry "grantRevision" value.GrantRevision
                entry)
            |> List.toArray

        put items "authorizedApprovers" approvers

    let report (claims: RestoreReportClaims) =
        let items = fields ()
        identity items claims
        put items "format" "claimcore-restore-qualification-1"
        put items "source" "ClaimCore.Database"
        put items "scope" claims.Scope
        put items "realDataReady" claims.RealDataReady
        reportFacts items claims
        reportChecks items claims
        reportCustody items claims claims.CustodyKeyId claims.CustodyPublicKeySha256
        reportApprovers items claims
        bytes items

    let produce claims index publication =
        let indexBytes = evidenceIndex claims index publication
        let indexDigest = SHA256.HashData(indexBytes) |> Convert.ToHexStringLower

        if claims.EvidenceIndexSha256 <> indexDigest then
            invalidOp "The restore report does not bind the produced evidence index."

        let reportBytes = report claims

        if DatabaseRestoreReportClaims.parse reportBytes claims.CheckedAt |> Option.isNone then
            invalidOp "Produced restore report failed its independent parser."

        if DatabaseRestoreEvidenceIndex.parse indexBytes claims |> Option.isNone then
            invalidOp "Produced restore evidence index failed its independent parser."

        {
            Report = reportBytes
            EvidenceIndex = indexBytes
            ReportSha256 = SHA256.HashData(reportBytes) |> Convert.ToHexStringLower
            EvidenceIndexSha256 = indexDigest
        }
