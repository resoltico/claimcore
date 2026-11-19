namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

type AttachmentReadRequest =
    {
        Target: CaseSelector
        AttachmentId: AttachmentId
    }

type AttachmentContent =
    {
        Bytes: VerifiedAttachment
        Preview: AttachmentPreview option
    }

type AttachmentRow =
    {
        AttachmentId: AttachmentId
        Name: AttachmentName
        Length: int64
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        CanWithdraw: bool
        CanPreview: bool
    }

/// A case can have at most AttachmentLimits.RetainedFiles current attachments.
type AttachmentPage =
    {
        Basis: WorkDateBasis
        CaseId: CaseId
        Reference: CaseReference
        Disclosure: Sequence
        Items: AttachmentRow list
        CanAdd: bool
    }
