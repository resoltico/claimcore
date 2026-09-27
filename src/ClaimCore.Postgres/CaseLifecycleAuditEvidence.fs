namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.Witness

module internal CaseLifecycleAuditEvidence =
    let private reject () =
        raise (InvalidDataException("Lifecycle authority evidence differs."))

    let private identityMatches
        caseId
        sequence
        (row: LifecycleAuditEventRow)
        (decoded: LifecycleAuditDecodedEvent)
        =
        row.CaseId = caseId
        && decoded.CaseId = caseId
        && row.EventId = decoded.Change.EventId
        && row.Reference = decoded.Change.CaseReference
        && row.Sequence = sequence
        && decoded.Sequence = sequence
        && row.BusinessRevision = decoded.BusinessRevision
        && row.ActionName = CaseLifecycleCandidate.actionName decoded.Change.Action
        && row.ActorId = decoded.ActorId
        && row.GrantRevision = decoded.GrantRevision
        && row.GrantRevision > 0L
        && row.ActorExists

    let private proofMatches
        cutoff
        (previousHash: byte array)
        sequence
        draftHash
        candidateHash
        eventHash
        (row: LifecycleAuditEventRow)
        (decoded: LifecycleAuditDecodedEvent)
        =
        decoded.Change.ExpectedLifecycleSequence = sequence - 1L
        && decoded.Change.ExpectedLifecycleHash = Convert.ToHexStringLower previousHash
        && row.DraftHash = draftHash
        && row.CandidateHash = candidateHash
        && row.PreviousHash = previousHash
        && decoded.PreviousHash = previousHash
        && row.EventHash = eventHash
        && row.WitnessSequence <= cutoff

    let validateApproval cutoff caseId (row: LifecycleAuditApprovalRow) =
        let expected =
            CaseLifecycleCandidate.approval
                row.ApprovalId
                row.OperationId
                row.CaseId
                row.DraftHash
                row.ApproverId
                row.GrantRevision
                row.ApprovedAt
                row.ExpiresAt

        try
            if
                row.CaseId <> caseId
                || not row.Human
                || row.GrantRevision <= 0L
                || row.ApprovedAt.Offset <> TimeSpan.Zero
                || row.ExpiresAt.Offset <> TimeSpan.Zero
                || row.ExpiresAt <= row.ApprovedAt
                || row.ExpiresAt - row.ApprovedAt > TimeSpan.FromHours 24.0
                || row.WitnessSequence > cutoff
                || row.Canonical <> expected
                || row.CandidateHash <> SHA256.HashData(row.Canonical)
            then
                reject ()

        finally
            CryptographicOperations.ZeroMemory(expected)

    let approval (witness: WitnessProtocol) cutoff caseId (row: LifecycleAuditApprovalRow) =
        validateApproval cutoff caseId row

        witness.VerifyAuthorityEvidenceForCase(
            row.ApprovalId,
            row.WitnessSequence,
            row.WitnessEpoch,
            row.WitnessHash,
            row.CandidateHash,
            caseId
        )

    let validateEvent cutoff caseId previousHash sequence (row: LifecycleAuditEventRow) =
        let decoded = CaseLifecycleAuditCodec.decodeEvent row.Canonical
        let draftHash = SHA256.HashData(decoded.Draft)
        let candidateHash = SHA256.HashData(row.Canonical)
        let eventHash = CaseLifecycleCandidate.eventHash previousHash candidateHash

        if
            not (identityMatches caseId sequence row decoded)
            || not (
                proofMatches
                    cutoff
                    previousHash
                    sequence
                    draftHash
                    candidateHash
                    eventHash
                    row
                    decoded
            )
        then
            reject ()

        decoded, candidateHash

    let event
        (witness: WitnessProtocol)
        cutoff
        caseId
        previousHash
        sequence
        (row: LifecycleAuditEventRow)
        =
        let decoded, candidateHash = validateEvent cutoff caseId previousHash sequence row

        witness.VerifyAuthorityEvidenceForCase(
            row.EventId,
            row.WitnessSequence,
            row.WitnessEpoch,
            row.WitnessHash,
            candidateHash,
            caseId
        )

        decoded
