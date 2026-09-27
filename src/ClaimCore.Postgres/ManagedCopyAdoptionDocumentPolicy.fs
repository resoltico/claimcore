namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal ParsedCopyAdoptionDocuments =
    {
        Custody: CopyAdoptionCustody
        Registry: CopyAdoptionRegistryReceipt
        Inspection: CopyAdoptionInspectionReceipt
    }

/// All three independently signed receipts must attest the same pre-fence origin, exact
/// claimant-copy digest and private location commitments. PRESENT is custody, not absence.
module internal ManagedCopyAdoptionDocumentPolicy =
    let parse (submission: CopyAdoptionSubmission) =
        match
            ManagedCopyAdoptionCustody.parse submission.Custodian.Canonical,
            ManagedCopyAdoptionRegistryReceipt.parse submission.Registry.Canonical,
            ManagedCopyAdoptionInspectionReceipt.parse submission.Inspection.Canonical
        with
        | Some custody, Some registry, Some inspection ->
            Some
                {
                    Custody = custody
                    Registry = registry
                    Inspection = inspection
                }
        | _ -> None

    let private custodyMatches
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (custody: CopyAdoptionCustody)
        (previousHash: byte array)
        (now: DateTimeOffset)
        =
        custody.AdoptionEventId = submission.AdoptionEventId
        && custody.CopyId = request.CopyId
        && custody.CaseId = request.CaseId
        && custody.OwnerApprovalId = request.ApprovalId
        && custody.Origin = request.Origin
        && custody.InstallationId = witness.Identity.InstallationId
        && custody.LineageId = witness.Identity.LineageId
        && custody.Epoch = witness.Identity.Epoch
        && custody.PreviousEventHash = previousHash
        && custody.CiphertextSha256 = request.CiphertextSha256
        && custody.CiphertextBytes = request.CiphertextBytes
        && custody.CapturedAt = request.CapturedAt
        && custody.RetainUntil = request.RetainUntil
        && custody.LocationCommitment = request.LocationCommitment
        && custody.CustodianCommitment = request.CustodianCommitment
        && custody.SigningKeyId = request.CustodianSigningKeyId
        && custody.ValidUntil > now
        && custody.ValidUntil <= request.ExpiresAt

    let private registryMatches
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (registry: CopyAdoptionRegistryReceipt)
        (now: DateTimeOffset)
        =
        registry.AdoptionEventId = submission.AdoptionEventId
        && registry.CopyId = request.CopyId
        && registry.CaseId = request.CaseId
        && registry.LocationCommitment = request.LocationCommitment
        && registry.CustodianCommitment = request.CustodianCommitment
        && registry.CustodianCanonicalSha256 = SHA256.HashData(submission.Custodian.Canonical)
        && registry.SigningKeyId = request.RegistrySigningKeyId
        && registry.ObservedAt <= now
        && registry.ValidUntil > now
        && registry.ValidUntil <= registry.ObservedAt.AddMinutes(10.0)

    let private inspectionMatches
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (registry: CopyAdoptionRegistryReceipt)
        (inspection: CopyAdoptionInspectionReceipt)
        (now: DateTimeOffset)
        =
        inspection.AdoptionEventId = submission.AdoptionEventId
        && inspection.CopyId = request.CopyId
        && inspection.CaseId = request.CaseId
        && inspection.LocationCommitment = request.LocationCommitment
        && inspection.RegistryCanonicalSha256 = SHA256.HashData(submission.Registry.Canonical)
        && inspection.CiphertextSha256 = request.CiphertextSha256
        && inspection.CiphertextBytes = request.CiphertextBytes
        && inspection.SigningKeyId = request.InspectorSigningKeyId
        && inspection.ObservedAt >= registry.ObservedAt
        && inspection.ObservedAt <= now
        && inspection.ValidUntil > now
        && inspection.ValidUntil <= inspection.ObservedAt.AddMinutes(5.0)

    let private privateLocationMatches
        (request: CopyAdoptionApprovalRequest)
        (inspection: CopyAdoptionInspectionReceipt)
        (proof: VerifiedCopyAdoptionPrivateLocation)
        (now: DateTimeOffset)
        =
        proof.CopyId = request.CopyId
        && proof.CaseId = request.CaseId
        && proof.LocationCommitment = request.LocationCommitment
        && proof.CustodianCommitment = request.CustodianCommitment
        && proof.CiphertextSha256 = request.CiphertextSha256
        && proof.CiphertextBytes = request.CiphertextBytes
        && proof.ObservedAt >= inspection.ObservedAt
        && proof.ObservedAt <= now
        && proof.ExpiresAt > now

    let retainedMatches
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (parsed: ParsedCopyAdoptionDocuments)
        (previousHash: byte array)
        (now: DateTimeOffset)
        =
        let custody = parsed.Custody
        let registry = parsed.Registry
        let inspection = parsed.Inspection

        submission.ApprovalId = request.ApprovalId
        && submission.AdoptionEventId = request.AdoptionEventId
        && request.CustodianCanonicalSha256 = SHA256.HashData(submission.Custodian.Canonical)
        && request.RegistryCanonicalSha256 = SHA256.HashData(submission.Registry.Canonical)
        && request.InspectionReportSha256 = SHA256.HashData(submission.Inspection.Canonical)
        && custodyMatches witness request submission custody previousHash now
        && registryMatches request submission registry now
        && inspectionMatches request submission registry inspection now

    let matches witness request submission parsed previousHash proof now =
        retainedMatches witness request submission parsed previousHash now
        && privateLocationMatches request parsed.Inspection proof now
