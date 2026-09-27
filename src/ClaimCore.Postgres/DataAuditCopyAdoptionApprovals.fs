namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Replays every witnessed human-OWNER custody-adoption draft, including unused drafts.
/// An approval is not proof that a product export or external copy was adopted or deleted.
module internal DataAuditCopyAdoptionApprovals =
    let private sourceMatches
        (row: CopyAdoptionApprovalAuditRow)
        (request: CopyAdoptionApprovalRequest)
        =
        match request.Origin with
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            row.OriginKind = "PRODUCT_EXPORT"
            && row.PreFenceKind = "PRODUCT_EXPORT_RECEIPT"
            && row.ExportId = Some exportId
            && exportId = request.CopyId
            && row.ExportCaseId = Some request.CaseId
            && row.ExportSha256 = Some request.CiphertextSha256
            && row.ExportSequence = Some sequence
            && row.ExportHash = Some hash
            && row.ExportIssuedAt = Some request.CapturedAt
        | CopyAdoptionOrigin.AdoptedExternal _ ->
            row.OriginKind = "ADOPTED_EXTERNAL"
            && row.PreFenceKind = "PUBLISHED_REGISTRY"
            && row.ExportId.IsNone
            && row.ExportCaseId.IsNone
            && row.ExportSha256.IsNone
            && row.ExportSequence.IsNone
            && row.ExportHash.IsNone
            && row.ExportIssuedAt.IsNone

    let private fieldsMatch
        (row: CopyAdoptionApprovalAuditRow)
        (decoded: DecodedCopyAdoptionApproval)
        =
        let request = decoded.Request

        ManagedCopyAdoptionApprovalPolicy.validDraft request
        && row.ApprovalId = request.ApprovalId
        && row.AdoptionEventId = request.AdoptionEventId
        && row.CopyId = request.CopyId
        && row.CaseId = request.CaseId
        && row.CiphertextSha256 = request.CiphertextSha256
        && row.CiphertextBytes = request.CiphertextBytes
        && row.PreFenceHash =
            (match request.Origin with
             | CopyAdoptionOrigin.ProductExport(_, _, hash)
             | CopyAdoptionOrigin.AdoptedExternal(_, hash) -> hash)
        && row.PreFenceSequence =
            (match request.Origin with
             | CopyAdoptionOrigin.ProductExport(_, sequence, _)
             | CopyAdoptionOrigin.AdoptedExternal(sequence, _) -> sequence)
        && row.LocationCommitment = request.LocationCommitment
        && row.RetainUntil = request.RetainUntil
        && row.ActorId = decoded.ActorId
        && row.GrantRevision = decoded.GrantRevision
        && row.ApprovedAt = decoded.ApprovedAt
        && row.ExpiresAt = request.ExpiresAt
        && sourceMatches row request

    let private chronology (row: CopyAdoptionApprovalAuditRow) cutoff =
        row.GrantRevision > 0L
        && row.ApprovedAt.Offset = TimeSpan.Zero
        && row.ExpiresAt > row.ApprovedAt
        && row.ExpiresAt <= row.ApprovedAt.AddHours(24.0)
        && row.PreFenceSequence < row.RequestSequence
        && row.PreFenceSequence < row.WitnessSequence
        && row.RequestSequence < row.WitnessSequence
        && row.WitnessSequence <= cutoff
        && (row.UsedEventId |> Option.forall ((=) row.AdoptionEventId))

    let private verifyShape (witness: WitnessProtocol) cutoff (row: CopyAdoptionApprovalAuditRow) =
        let decoded =
            ManagedCopyAdoptionApprovalCodec.decode row.Canonical
            |> Option.defaultWith corrupt

        if
            not (fieldsMatch row decoded)
            || not (chronology row cutoff)
            || row.WitnessEpoch <> witness.Identity.Epoch
            || row.CandidateHash <> SHA256.HashData(row.Canonical)
            || decoded.Request.CapturedAt >= row.RequestedAt
        then
            corrupt ()

        decoded

    let private verifySource
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: CopyAdoptionApprovalAuditRow)
        (decoded: DecodedCopyAdoptionApproval)
        (ct: CancellationToken)
        =
        task {
            witnessProof (fun () ->
                witness.VerifyHistoricalTip(row.PreFenceSequence, row.PreFenceHash))

            match decoded.Request.Origin with
            | CopyAdoptionOrigin.ProductExport _ -> ()
            | CopyAdoptionOrigin.AdoptedExternal _ ->
                let! origin =
                    ManagedCopyExternalPublicationOrigin.verify
                        connection
                        transaction
                        witness
                        cutoff
                        decoded.Request
                        ct

                if not origin then
                    corrupt ()
        }

    let private verifyEvidence
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: CopyAdoptionApprovalAuditRow)
        (decoded: DecodedCopyAdoptionApproval)
        (ct: CancellationToken)
        =
        task {
            do!
                DataAuditCopyAdoptionOwnerRole.verify
                    connection
                    transaction
                    row.ActorId
                    row.CaseId
                    row.GrantRevision
                    ct

            do!
                DataAuditCopyAdoptionSignerRole.verify
                    connection
                    transaction
                    decoded.Request.CustodianSigningKeyId
                    decoded.Request.RegistrySigningKeyId
                    decoded.Request.InspectorSigningKeyId
                    row.ActorId
                    row.WitnessSequence
                    ct

            do! verifySource connection transaction witness cutoff row decoded ct

            do!
                CaseWitnessAuditEvidence.verify
                    connection
                    transaction
                    witness
                    cutoff
                    row.CaseId
                    row.ApprovalId
                    row.WitnessSequence
                    row.WitnessEpoch
                    row.WitnessHash
                    row.CandidateHash
                    SettledAuthority
        }

    let private verifyRow connection transaction witness cutoff row ct =
        let decoded = verifyShape witness cutoff row
        verifyEvidence connection transaction witness cutoff row decoded ct

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows = DataAuditCopyAdoptionApprovalRows.page connection transaction after ct

                for row in rows do
                    if row.ApprovalId <= after then
                        corrupt ()

                    do! verifyRow connection transaction witness cutoff row ct
                    after <- row.ApprovalId
                    count <- count + 1L

                more <- rows.Length = 50

            return count
        }
