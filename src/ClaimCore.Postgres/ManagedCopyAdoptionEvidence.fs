namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Verified immutable adoption origin only. A later copy transition, current private location
/// and actual absence still require their separate signed replay; this is not deletion proof.
[<NoEquality; NoComparison>]
type internal VerifiedCopyAdoptionOrigin =
    {
        AdoptionEventId: Guid
        CopyId: Guid
        CaseId: Guid
        ProducerKind: string
        CopyRevision: int64
        CopyEventHash: byte array
        CustodianSigningKeyId: Guid
        CustodianHolderActorId: Guid
        LocationCommitment: byte array
        CustodianCommitment: byte array
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        EncryptionKeyId: Guid
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
    }

module internal ManagedCopyAdoptionEvidence =
    let private sourceMatches
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        =
        let c = receipt.Core
        let s = receipt.Signatures
        let request = authority.Approval.Request
        let custody = authority.Documents.Custody

        c.EventId = request.AdoptionEventId
        && c.CopyId = request.CopyId
        && c.CaseId = request.CaseId
        && c.CiphertextSha256 = request.CiphertextSha256
        && c.CiphertextBytes = request.CiphertextBytes
        && c.CapturedAt = request.CapturedAt
        && c.RetainUntil = request.RetainUntil
        && c.LocationCommitment = request.LocationCommitment
        && c.CustodianCommitment = request.CustodianCommitment
        && c.CustodianKeyId = request.CustodianSigningKeyId
        && c.RegistryKeyId = request.RegistrySigningKeyId
        && c.InspectorKeyId = request.InspectorSigningKeyId
        && s.InspectionReportSha256 = request.InspectionReportSha256
        && c.CopyRevision = custody.Revision
        && c.ExecutorKind = "SCHEMA_OWNER_PROCESS"
        && c.ActorAuthorityRevision > 0L

    let private originMatches
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        =
        let c = receipt.Core

        match authority.Approval.Request.Origin with
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            c.OriginKind = "PRODUCT_EXPORT"
            && c.PreFenceKind = "PRODUCT_EXPORT_RECEIPT"
            && c.ExportId = Some exportId
            && c.CopyId = exportId
            && c.CopyRevision = 2L
            && c.PreFenceSequence = sequence
            && c.PreFenceHash = hash
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            c.OriginKind = "ADOPTED_EXTERNAL"
            && c.PreFenceKind = "PUBLISHED_REGISTRY"
            && c.ExportId.IsNone
            && c.CopyRevision = 1L
            && c.PreFenceSequence = sequence
            && c.PreFenceHash = hash

    let private candidate
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        revision
        hash
        privateExpiry
        =
        {
            Approval = authority.Approval
            Submission = authority.Submission
            Documents = authority.Documents
            CustodianSigner = authority.CustodianSigner
            RegistrySigner = authority.RegistrySigner
            InspectorSigner = authority.InspectorSigner
            PreviousEventHash = receipt.Witness.PreviousCopyHash
            CopyEventHash = receipt.Witness.CopyEventHash
            CaseAuthorityRevision = revision
            CaseAuthorityHash = hash
            ActorAuthorityRevision = receipt.Core.ActorAuthorityRevision
            ObservedAt = receipt.Witness.AdoptedAt
            PrivateLocationExpiresAt = privateExpiry
        }

    let private witnessShape
        (witness: WitnessProtocol)
        cutoff
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        expected
        =
        let c = receipt.Core
        let w = receipt.Witness

        w.Canonical = expected
        && w.CandidateHash = SHA256.HashData(expected)
        && w.Sequence <= cutoff
        && w.Epoch = witness.Identity.Epoch
        && w.Sequence > authority.Approval.WitnessSequence
        && w.Sequence > c.PreFenceSequence
        && w.ValidUntil > w.AdoptedAt
        && w.ValidUntil = authority.Documents.Custody.ValidUntil

    let private verifyWitness
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        =
        task {
            let c = receipt.Core
            let w = receipt.Witness

            let revision, hash, privateExpiry =
                ManagedCopyAdoptionOwnerCodec.authorityTip w.Canonical
                |> Option.defaultWith corrupt

            let expected =
                ManagedCopyAdoptionOwnerCandidate.encode (
                    candidate receipt authority revision hash privateExpiry
                )

            try
                if not (witnessShape witness cutoff receipt authority expected) then
                    corrupt ()

                do!
                    ManagedCopyAdoptionAuthorityTip.verify
                        connection
                        transaction
                        c.CaseId
                        revision
                        hash
                        w.AdoptedAt
                        w.Sequence

                witnessProof (fun () ->
                    witness.VerifyHistoricalTip(c.PreFenceSequence, c.PreFenceHash))

                do!
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        cutoff
                        c.CaseId
                        c.EventId
                        w.Sequence
                        w.Epoch
                        w.EntryHash
                        w.CandidateHash
                        SettledAuthority
            finally
                CryptographicOperations.ZeroMemory(expected)
        }

    let private projected (receipt: CopyAdoptionReceipt) (authority: CopyAdoptionAuthorityProof) =
        let c = receipt.Core
        let w = receipt.Witness
        let custody = authority.Documents.Custody

        {
            AdoptionEventId = c.EventId
            CopyId = c.CopyId
            CaseId = c.CaseId
            ProducerKind = c.OriginKind
            CopyRevision = c.CopyRevision
            CopyEventHash = Array.copy w.CopyEventHash
            CustodianSigningKeyId = c.CustodianKeyId
            CustodianHolderActorId = c.CustodianHolderId
            LocationCommitment = Array.copy c.LocationCommitment
            CustodianCommitment = Array.copy c.CustodianCommitment
            CiphertextSha256 = Array.copy c.CiphertextSha256
            CiphertextBytes = c.CiphertextBytes
            CapturedAt = c.CapturedAt
            RetainUntil = c.RetainUntil
            EncryptionKeyId = custody.EncryptionKeyId
            WitnessSequence = w.Sequence
            WitnessEpoch = w.Epoch
            WitnessEntryHash = Array.copy w.EntryHash
        }

    let private verifyExternalOrigin
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (receipt: CopyAdoptionReceipt)
        (authority: CopyAdoptionAuthorityProof)
        (ct: CancellationToken)
        =
        task {
            match authority.Approval.Request.Origin with
            | CopyAdoptionOrigin.ProductExport _ -> ()
            | CopyAdoptionOrigin.AdoptedExternal _ ->
                let! origin =
                    ManagedCopyExternalPublicationOrigin.verify
                        connection
                        transaction
                        witness
                        cutoff
                        authority.Approval.Request
                        ct

                let! published =
                    ManagedCopyExternalPublicationEvidence.verifyCopy
                        connection
                        transaction
                        witness
                        cutoff
                        receipt.Core.CopyId
                        ct

                if
                    not origin
                    || (published |> Option.map _.EncryptionKeyId)
                       <> Some authority.Documents.Custody.EncryptionKeyId
                then
                    corrupt ()
        }

    /// None is a genuinely absent adoption receipt; an existing corrupt receipt always throws.
    let verifyOrigin
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        copyId
        (ct: CancellationToken)
        =
        task {
            let! found = ManagedCopyAdoptionEvidenceRows.find connection transaction copyId

            match found with
            | None -> return None
            | Some receipt ->
                let! authority =
                    ManagedCopyAdoptionEvidenceAuthority.verify
                        connection
                        transaction
                        witness
                        cutoff
                        receipt
                        ct

                if not (sourceMatches receipt authority && originMatches receipt authority) then
                    corrupt ()

                do! verifyExternalOrigin connection transaction witness cutoff receipt authority ct

                do! verifyWitness connection transaction witness cutoff receipt authority

                do!
                    ManagedCopyAdoptionEventEvidence.verify
                        connection
                        transaction
                        receipt
                        authority.Documents.Custody

                return Some(projected receipt authority)
        }
