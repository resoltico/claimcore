namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal CopyAdoptionAuthorityProof =
    {
        Approval: ApprovedCopyAdoption
        Documents: ParsedCopyAdoptionDocuments
        Submission: CopyAdoptionSubmission
        CustodianSigner: CopyAdoptionSignerEvidence
        RegistrySigner: CopyAdoptionSignerEvidence
        InspectorSigner: CopyAdoptionSignerEvidence
    }

/// Historical signer and owner authority remains auditable after later key/role revocation.
module internal ManagedCopyAdoptionEvidenceAuthority =
    let private submitted (receipt: CopyAdoptionReceipt) =
        {
            ApprovalId = receipt.Core.OwnerApprovalId
            AdoptionEventId = receipt.Core.EventId
            Custodian =
                {
                    Canonical = receipt.Signatures.CustodianCanonical
                    Signature = receipt.Signatures.CustodianSignature
                }
            Registry =
                {
                    Canonical = receipt.Signatures.RegistryCanonical
                    Signature = receipt.Signatures.RegistrySignature
                }
            Inspection =
                {
                    Canonical = receipt.Signatures.InspectionCanonical
                    Signature = receipt.Signatures.InspectionSignature
                }
        }

    let private useRow connection transaction (receipt: CopyAdoptionReceipt) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT adoption_event_id,case_id FROM "
                    + "claimcore.managed_copy_adoption_approval_uses "
                    + "WHERE approval_id=@approval",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" receipt.Core.OwnerApprovalId
            use! reader = command.ExecuteReaderAsync()

            if
                not (reader.Read())
                || reader.GetGuid(0) <> receipt.Core.EventId
                || reader.GetGuid(1) <> receipt.Core.CaseId
                || reader.Read()
            then
                corrupt ()
        }

    let private approvalShape
        (stored: StoredAdoptionApproval)
        (decoded: DecodedCopyAdoptionApproval)
        (receipt: CopyAdoptionReceipt)
        =
        let request = decoded.Request
        let c = receipt.Core
        let w = receipt.Witness

        stored.CandidateHash = SHA256.HashData(stored.Canonical)
        && stored.ActorId = c.OwnerActorId
        && stored.GrantRevision = c.OwnerGrantRevision
        && decoded.ActorId = c.OwnerActorId
        && decoded.GrantRevision = c.OwnerGrantRevision
        && decoded.ApprovedAt = stored.ApprovedAt
        && stored.WitnessSequence < w.Sequence
        && stored.ExpiresAt > w.AdoptedAt
        && request.ApprovalId = c.OwnerApprovalId
        && request.AdoptionEventId = c.EventId
        && request.CopyId = c.CopyId
        && request.CaseId = c.CaseId

    let private approvalWitness
        connection
        transaction
        witness
        cutoff
        (receipt: CopyAdoptionReceipt)
        (stored: StoredAdoptionApproval)
        ct
        =
        CaseWitnessAuditEvidence.verify
            connection
            transaction
            witness
            cutoff
            receipt.Core.CaseId
            receipt.Core.OwnerApprovalId
            stored.WitnessSequence
            stored.WitnessEpoch
            stored.WitnessHash
            stored.CandidateHash
            SettledAuthority
            ct

    let private ownerRoles connection transaction (receipt: CopyAdoptionReceipt) revision ct =
        task {
            do!
                DataAuditCopyAdoptionOwnerRole.verify
                    connection
                    transaction
                    receipt.Core.OwnerActorId
                    receipt.Core.CaseId
                    revision
                    ct

            do!
                DataAuditCopyAdoptionOwnerRole.verify
                    connection
                    transaction
                    receipt.Core.OwnerActorId
                    receipt.Core.CaseId
                    receipt.Core.ActorAuthorityRevision
                    ct
        }

    let private approval
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (receipt: CopyAdoptionReceipt)
        ct
        =
        task {
            let! found =
                ManagedCopyAdoptionApprovalRead.find
                    connection
                    transaction
                    receipt.Core.OwnerApprovalId

            let stored = found |> Option.defaultWith corrupt

            let decoded =
                ManagedCopyAdoptionApprovalCodec.decode stored.Canonical
                |> Option.defaultWith corrupt

            let request = decoded.Request
            let c = receipt.Core

            if not (approvalShape stored decoded receipt) then
                corrupt ()

            do! ownerRoles connection transaction receipt stored.GrantRevision ct

            do! approvalWitness connection transaction witness cutoff receipt stored ct

            do! useRow connection transaction receipt

            return
                {
                    Request = request
                    ActorId = c.OwnerActorId
                    GrantRevision = c.OwnerGrantRevision
                    CandidateHash = stored.CandidateHash
                    WitnessSequence = stored.WitnessSequence
                }
        }

    let private signer connection transaction key purpose canonical signature sequence ct =
        task {
            let! value =
                DataAuditCopyAdoptionSignerRole.read connection transaction key purpose sequence ct

            if not (ManagedCopySignature.verify value.PublicKey canonical signature) then
                corrupt ()

            return
                {
                    KeyId = key
                    HolderId = value.Holder
                    RegisteredSequence = value.RegisteredSequence
                    RetiredSequence = value.RetiredSequence
                }
        }

    let private signers connection transaction (receipt: CopyAdoptionReceipt) ct =
        task {
            let c = receipt.Core
            let s = receipt.Signatures

            let! custodian =
                signer
                    connection
                    transaction
                    c.CustodianKeyId
                    "COPY_ATTESTOR"
                    s.CustodianCanonical
                    s.CustodianSignature
                    receipt.Witness.Sequence
                    ct

            let! registry =
                signer
                    connection
                    transaction
                    c.RegistryKeyId
                    "LOCATION_REGISTRY"
                    s.RegistryCanonical
                    s.RegistrySignature
                    receipt.Witness.Sequence
                    ct

            let! inspector =
                signer
                    connection
                    transaction
                    c.InspectorKeyId
                    "LOCATION_INSPECTOR"
                    s.InspectionCanonical
                    s.InspectionSignature
                    receipt.Witness.Sequence
                    ct

            if
                not (
                    ManagedCopyAdoptionSignatureEvidence.distinct
                        c.OwnerActorId
                        custodian
                        registry
                        inspector
                )
                || c.CustodianHolderId <> custodian.HolderId
                || c.RegistryHolderId <> registry.HolderId
                || c.InspectorHolderId <> inspector.HolderId
            then
                corrupt ()

            return custodian, registry, inspector
        }

    let verify connection transaction witness cutoff (receipt: CopyAdoptionReceipt) ct =
        task {
            let submission = submitted receipt

            let documents =
                ManagedCopyAdoptionDocumentPolicy.parse submission |> Option.defaultWith corrupt

            let! approved = approval connection transaction witness cutoff receipt ct
            let! custodian, registry, inspector = signers connection transaction receipt ct

            if
                not (
                    ManagedCopyAdoptionDocumentPolicy.retainedMatches
                        witness
                        approved.Request
                        submission
                        documents
                        receipt.Witness.PreviousCopyHash
                        receipt.Witness.AdoptedAt
                )
            then
                corrupt ()

            return
                {
                    Approval = approved
                    Documents = documents
                    Submission = submission
                    CustodianSigner = custodian
                    RegistrySigner = registry
                    InspectorSigner = inspector
                }
        }
