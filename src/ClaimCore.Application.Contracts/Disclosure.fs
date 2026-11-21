namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type DisclosureKind =
    | CaseRead
    | AttachmentDownload
    | AttachmentPreview
    | Export
    | TrailRead
    | AccessRefused

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DisclosureKind =
    let vocabulary =
        Vocabulary.ofUnion<DisclosureKind> "disclosureKind" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type DisclosureOutcome =
    | Succeeded
    | Refused

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DisclosureOutcome =
    let vocabulary =
        Vocabulary.ofUnion<DisclosureOutcome> "disclosureOutcome" TokenCasing.ScreamingSnake None

type DisclosureExport =
    {
        Query: string
        Selection: Map<string, string>
        RowCount: int
    }

type DisclosureEvent =
    {
        ActorId: ActorId
        Kind: DisclosureKind
        CaseId: CaseId option
        AttachmentId: AttachmentId option
        Outcome: DisclosureOutcome
        Export: DisclosureExport option
    }

type RecordedDisclosure =
    {
        Id: int64
        RecordedAt: DateTimeOffset
        Event: DisclosureEvent
    }

type DisclosureTrailRequest =
    {
        ActorId: ActorId option
        CaseId: CaseId option
        Kind: DisclosureKind option
        From: DateTimeOffset option
        Until: DateTimeOffset option
        AfterCursor: string option
        Limit: int
    }

type DisclosureTrailPage =
    {
        Basis: WorkDateBasis
        Items: RecordedDisclosure list
        NextCursor: string option
    }

module DisclosureTrail =
    let retentionMonths = 24

    let cutoff (observedAt: DateTimeOffset) =
        observedAt.ToUniversalTime().AddMonths(-retentionMonths)
