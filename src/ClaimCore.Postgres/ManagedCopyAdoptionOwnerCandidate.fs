namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal CopyAdoptionOwnerEventData =
    {
        Approval: ApprovedCopyAdoption
        Submission: CopyAdoptionSubmission
        Documents: ParsedCopyAdoptionDocuments
        CustodianSigner: CopyAdoptionSignerEvidence
        RegistrySigner: CopyAdoptionSignerEvidence
        InspectorSigner: CopyAdoptionSignerEvidence
        PreviousEventHash: byte array
        CopyEventHash: byte array
        CaseAuthorityRevision: int64
        CaseAuthorityHash: byte array
        ActorAuthorityRevision: int64
        ObservedAt: DateTimeOffset
        PrivateLocationExpiresAt: DateTimeOffset
    }

/// Nonpayload owner event binds all retained signed receipts, the current authority tips,
/// original producer chain and one-use human approval before witness intent/co-commit.
module internal ManagedCopyAdoptionOwnerCandidate =
    let private hex (value: byte array) = Convert.ToHexStringLower value

    let private origin (writer: Utf8JsonWriter) =
        function
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            writer.WriteString("originKind", "PRODUCT_EXPORT")
            writer.WriteString("exportId", exportId)
            writer.WriteNumber("preFenceSequence", sequence)
            writer.WriteString("preFenceHash", hex hash)
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            writer.WriteString("originKind", "ADOPTED_EXTERNAL")
            writer.WriteNull("exportId")
            writer.WriteNumber("preFenceSequence", sequence)
            writer.WriteString("preFenceHash", hex hash)

    let private signed (writer: Utf8JsonWriter) (value: CopyAdoptionOwnerEventData) =
        writer.WriteString(
            "custodianCanonicalSha256",
            hex (SHA256.HashData(value.Submission.Custodian.Canonical))
        )

        writer.WriteString(
            "custodianSignatureSha256",
            hex (SHA256.HashData(value.Submission.Custodian.Signature))
        )

        writer.WriteString(
            "registryCanonicalSha256",
            hex (SHA256.HashData(value.Submission.Registry.Canonical))
        )

        writer.WriteString(
            "registrySignatureSha256",
            hex (SHA256.HashData(value.Submission.Registry.Signature))
        )

        writer.WriteString(
            "inspectionCanonicalSha256",
            hex (SHA256.HashData(value.Submission.Inspection.Canonical))
        )

        writer.WriteString(
            "inspectionSignatureSha256",
            hex (SHA256.HashData(value.Submission.Inspection.Signature))
        )

        writer.WriteString("custodianHolderActorId", value.CustodianSigner.HolderId)
        writer.WriteString("registryHolderActorId", value.RegistrySigner.HolderId)
        writer.WriteString("inspectorHolderActorId", value.InspectorSigner.HolderId)

    let private authority (writer: Utf8JsonWriter) (value: CopyAdoptionOwnerEventData) =
        writer.WriteString("ownerApprovalId", value.Approval.Request.ApprovalId)
        writer.WriteString("ownerApprovalCandidateSha256", hex value.Approval.CandidateHash)
        writer.WriteString("ownerActorId", value.Approval.ActorId)
        writer.WriteNumber("ownerGrantRevision", value.Approval.GrantRevision)
        writer.WriteNumber("actorAuthorityRevision", value.ActorAuthorityRevision)
        writer.WriteNumber("caseAuthorityRevision", value.CaseAuthorityRevision)
        writer.WriteString("caseAuthorityHash", hex value.CaseAuthorityHash)

    let encode (value: CopyAdoptionOwnerEventData) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        let request = value.Approval.Request
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "ADOPT_MANAGED_COPY")
        writer.WriteString("executorKind", "SCHEMA_OWNER_PROCESS")
        writer.WriteString("adoptionEventId", value.Submission.AdoptionEventId)
        writer.WriteString("copyId", request.CopyId)
        writer.WriteString("caseId", request.CaseId)
        origin writer request.Origin
        writer.WriteNumber("copyRevision", value.Documents.Custody.Revision)
        writer.WriteString("previousCopyHash", hex value.PreviousEventHash)
        writer.WriteString("copyEventHash", hex value.CopyEventHash)
        writer.WriteString("locationCommitment", hex request.LocationCommitment)
        writer.WriteString("custodianCommitment", hex request.CustodianCommitment)
        writer.WriteString("ciphertextSha256", hex request.CiphertextSha256)
        writer.WriteNumber("ciphertextBytes", request.CiphertextBytes)
        writer.WriteString("capturedAt", request.CapturedAt.ToString("O"))
        writer.WriteString("retainUntil", request.RetainUntil.ToString("O"))
        signed writer value
        authority writer value
        writer.WriteString("observedAt", value.ObservedAt.ToString("O"))
        writer.WriteString("privateLocationExpiresAt", value.PrivateLocationExpiresAt.ToString("O"))
        writer.WriteString("validUntil", value.Documents.Custody.ValidUntil.ToString("O"))
        writer.WriteEndObject()
        writer.Flush()
        buffer.ToArray()
