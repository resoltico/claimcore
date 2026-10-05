namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open ClaimCore.Witness
open DataAuditCommon

module internal DataAuditSignerEvents =
    let private pairIdentityMatches
        keyId
        publicHash
        (row: SignerEventAuditRow)
        (owner: SignerApprovalEvidence)
        (custodian: SignerApprovalEvidence)
        =
        owner.ActorId = row.OwnerActorId
        && custodian.ActorId = row.CustodianActorId
        && owner.ActorId <> custodian.ActorId
        && owner.ActorRole = "OWNER"
        && (custodian.ActorRole = "AUDITOR_CUSTODIAN"
            || custodian.ActorRole = "DATA_STEWARD")
        && owner.SigningKeyId = keyId
        && custodian.SigningKeyId = keyId
        && owner.PublicKeySha256 = publicHash
        && custodian.PublicKeySha256 = publicHash

    let private pairAuthorityMatches
        (row: SignerEventAuditRow)
        (owner: SignerApprovalEvidence)
        (custodian: SignerApprovalEvidence)
        uses
        =
        owner.Purpose = row.Purpose
        && custodian.Purpose = row.Purpose
        && owner.HolderApprovalId = Some custodian.ApprovalId
        && custodian.HolderApprovalId.IsNone
        && row.HolderActorId = custodian.ActorId
        && ManagedCopySignerCandidate.actionName owner.Action = row.Action
        && ManagedCopySignerCandidate.actionName custodian.Action = row.Action
        && uses = [ "CUSTODIAN", row.CustodianApprovalId; "OWNER", row.OwnerApprovalId ]

    let private approvedPair
        connection
        transaction
        witness
        cutoff
        keyId
        publicHash
        (row: SignerEventAuditRow)
        ct
        =
        task {
            let! owner =
                DataAuditSignerApprovals.verifyOne
                    connection
                    transaction
                    witness
                    cutoff
                    row.OwnerApprovalId
                    ct

            let! custodian =
                DataAuditSignerApprovals.verifyOne
                    connection
                    transaction
                    witness
                    cutoff
                    row.CustodianApprovalId
                    ct

            let! uses = DataAuditSignerRows.approvalUses connection transaction row.EventId ct

            if
                not (pairIdentityMatches keyId publicHash row owner custodian)
                || not (pairAuthorityMatches row owner custodian uses)
            then
                corrupt ()

            return owner, custodian
        }

    let private eventMatches
        (witness: WitnessProtocol)
        cutoff
        expectedRevision
        previous
        (canonical: byte array)
        hash
        (row: SignerEventAuditRow)
        =
        row.Revision = expectedRevision
        && row.Action = (if expectedRevision = 1L then "REGISTER" else "RETIRE")
        && row.Canonical = canonical
        && row.CandidateHash = SHA256.HashData(canonical)
        && row.PreviousHash = previous
        && row.EventHash = hash
        && row.WitnessEpoch = witness.Identity.Epoch
        && row.WitnessSequence <= cutoff

    let private verifyEvent
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        keyId
        publicHash
        previous
        expectedRevision
        (row: SignerEventAuditRow)
        (ct: CancellationToken)
        =
        task {
            let! owner, custodian =
                approvedPair connection transaction witness cutoff keyId publicHash row ct

            let canonical =
                ManagedCopySignerCandidate.roster
                    row.EventId
                    keyId
                    row.Action
                    row.Purpose
                    row.Revision
                    publicHash
                    owner.ActorId
                    custodian.ActorId
                    owner.ApprovalId
                    custodian.ApprovalId
                    owner.CandidateSha256
                    custodian.CandidateSha256

            let hash = ManagedCopySignerCandidate.eventHash previous canonical

            if not (eventMatches witness cutoff expectedRevision previous canonical hash row) then
                corrupt ()

            do!
                witnessProofAsync (fun () ->
                    witness.VerifyAuthorityEvidenceForInstallation(
                        row.EventId,
                        row.WitnessSequence,
                        row.WitnessEpoch,
                        row.WitnessHash,
                        row.CandidateHash,
                        ct
                    ))

            return hash
        }

    let private verifySigner
        connection
        transaction
        witness
        cutoff
        (signer: SignerProjectionAuditRow)
        ct
        =
        task {
            if
                signer.PublicHash <> SHA256.HashData(signer.PublicKey)
                || not (ManagedCopySignature.validPublicKey signer.PublicKey)
            then
                corrupt ()

            let! events = DataAuditSignerRows.events connection transaction signer.KeyId ct

            if events.IsEmpty || events.Length > 2 then
                corrupt ()

            let mutable previous = Array.zeroCreate<byte> 32
            let mutable revision = 0L

            for event in events do
                revision <- revision + 1L

                if
                    event.Purpose <> signer.Purpose || event.HolderActorId <> signer.HolderActorId
                then
                    corrupt ()

                let! hash =
                    verifyEvent
                        connection
                        transaction
                        witness
                        cutoff
                        signer.KeyId
                        signer.PublicHash
                        previous
                        revision
                        event
                        ct

                previous <- hash

            if
                signer.Revision <> revision
                || signer.Active <> (revision = 1L)
                || signer.EventHash <> previous
            then
                corrupt ()

            return revision
        }

    let verifyAll connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let! approvals =
                DataAuditSignerApprovals.verifyAll connection transaction witness cutoff ct

            let mutable after = Guid.Empty
            let mutable more = true
            let mutable signers = 0L
            let mutable events = 0L

            while more do
                let! page = DataAuditSignerRows.projectionPage connection transaction after ct

                for signer in page do
                    let! count = verifySigner connection transaction witness cutoff signer ct
                    signers <- signers + 1L
                    events <- events + count

                match List.tryLast page with
                | None -> more <- false
                | Some last -> after <- last.KeyId

            return approvals, signers, events
        }
