namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal HandoffReportEvidence =
    {
        Report: RestoreReportClaims
        Index: RestoreEvidenceIndex
        Publication: TrustedRestorePublication
        ReportSha256: string
    }

/// Exact historical pair and W1 ticket comparisons shared by preparation and settlement.
module internal DatabaseRestoreHandoffChecks =
    let digest (bytes: byte array) =
        SHA256.HashData(ReadOnlySpan<byte>(bytes)) |> Convert.ToHexStringLower

    let parsed publication scope (files: RestoreReportFiles) now =
        let report =
            DatabaseRestoreReportClaims.parse files.Report now
            |> Option.defaultWith (fun () -> invalidOp "Signed pre-W1 report is invalid.")

        if report.Scope <> scope || report.EvidenceIndexSha256 <> files.EvidenceIndexSha256 then
            invalidOp "Signed pre-W1 report scope or index differs."

        let index =
            DatabaseRestoreEvidenceIndex.parse files.EvidenceIndex report
            |> Option.defaultWith (fun () -> invalidOp "Signed pre-W1 index is invalid.")

        if publication.ManifestSha256 <> index.PublicationManifestSha256 then
            invalidOp "Signed pre-W1 publication differs."

        report, index

    let result publication report index (files: RestoreReportFiles) =
        {
            Report = report
            Index = index
            Publication = publication
            ReportSha256 = files.ReportSha256
        }

    let private publicationMatches
        (publication: TrustedRestorePublication)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        (proposal: WriterHandoffPreparation)
        binarySha256
        =
        publication.VerifierBinarySha256 = binarySha256
        && report.VerifierBinarySha256 = binarySha256
        && publication.ReportSignerKeyId = report.SignerKeyId
        && publication.CheckpointSignerKeyId = index.CheckpointSignerKeyId
        && publication.ReportSignerKeyId <> publication.CheckpointSignerKeyId
        && publication.InstallationId = facts.InstallationId
        && publication.LineageId = facts.LineageId
        && publication.Epoch = facts.Epoch
        && publication.WriterGeneration = proposal.OldGeneration
        && publication.WitnessCutoff = report.WitnessCutoff
        && publication.WitnessCutoffHash = report.WitnessCutoffHash

    let private reportMatches (report: RestoreReportClaims) (facts: RestoredPairFacts) =
        report.InstallationId = facts.InstallationId
        && report.LineageId = facts.LineageId
        && report.Epoch = facts.Epoch
        && report.PrimarySystemId = facts.PrimarySystemId
        && report.PrimaryTimeline = facts.PrimaryTimeline
        && report.WitnessSystemId = facts.WitnessSystemId
        && report.WitnessTimeline = facts.WitnessTimeline
        && report.CatalogManifestSha256 = facts.CatalogManifestSha256
        && report.AuthorityRevision <= facts.AuthorityRevision

    let private proposalMatches
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        (proposal: WriterHandoffPreparation)
        expectedGeneration
        =
        facts.WriterGeneration = expectedGeneration
        && proposal.InstallationId = facts.InstallationId
        && proposal.LineageId = facts.LineageId
        && proposal.Epoch = facts.Epoch
        && proposal.ReviewedCutoffSequence = report.WitnessCutoff
        && Convert.ToHexStringLower(proposal.ReviewedCutoffHash) = report.WitnessCutoffHash
        && Convert.ToHexStringLower(proposal.InventorySha256) = report.SignedInventoryFileSha256
        && proposal.CheckpointSigningKeyId = index.CheckpointSignerKeyId

    let historical publication report index facts proposal expectedGeneration binarySha256 =
        if
            not (publicationMatches publication report index facts proposal binarySha256)
            || not (reportMatches report facts)
            || not (proposalMatches report index facts proposal expectedGeneration)
        then
            invalidOp "Historical restored-pair report differs from exact W1 proposal."

    let auditedPair
        publication
        report
        index
        files
        proposal
        binarySha256
        facts
        barrier
        transaction
        (witness: WitnessProtocol)
        =
        WriterHandoffCutoff.verify witness proposal
        historical publication report index facts proposal proposal.OldGeneration binarySha256

        match witness.TryReadHashAtSequence(report.WitnessCutoff) with
        | Some hash when Convert.ToHexStringLower(hash) = report.WitnessCutoffHash -> ()
        | _ -> invalidOp "Pre-W1 cutoff is absent from the current witness."

        DatabaseRestoreReportRecheck.signedEvidence
            barrier
            transaction
            witness
            files
            report
            index
            facts

    let private ticketMatches summary (tip: Snapshot) (prepared: PrimaryWriterPreparation) ticket =
        summary.PendingIntents = 0L
        && tip.TipSequence = ticket.Sequence
        && tip.TipHash = ticket.EntryHash
        && prepared.Intent.Sequence = prepared.Value.ExpectedTipSequence + 1L
        && ticket.Sequence = prepared.Intent.Sequence + 1L

    let private settlementMatches
        handoffId
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        =
        value.HandoffId = handoffId
        && value.PrepareSequence = prepared.Intent.Sequence
        && value.PrepareHash = prepared.Intent.EntryHash
        && value.RestoreReportSha256 = prepared.Value.RestoreReportSha256
        && value.FenceReportSha256 = prepared.Value.FenceReportSha256
        && value.InventorySha256 = prepared.Value.InventorySha256

    let settled summary tip prepared value ticket handoffId reportSha256 =
        if
            not (ticketMatches summary tip prepared ticket)
            || not (settlementMatches handoffId prepared value)
            || reportSha256 <> Convert.ToHexStringLower(prepared.Value.RestoreReportSha256)
        then
            invalidOp "Post-W1 authority includes unreviewed change."
