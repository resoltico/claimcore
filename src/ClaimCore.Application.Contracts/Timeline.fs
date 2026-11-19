namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes

[<RequireQualifiedAccess>]
type TimelineFilter =
    | Facts
    | Work
    | Notes
    | Attachments

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TimelineFilter =
    let vocabulary =
        Vocabulary.ofUnion<TimelineFilter> "timelineFilter" TokenCasing.ScreamingSnake None

    let parts =
        function
        | TimelineFilter.Facts -> [ PartKind.Case; PartKind.Payment ]
        | TimelineFilter.Work -> [ PartKind.Work; PartKind.Reminder; PartKind.Link ]
        | TimelineFilter.Notes -> [ PartKind.Note ]
        | TimelineFilter.Attachments -> [ PartKind.Attachment ]

    let token = Vocabulary.token vocabulary

type CaseTimelineRequest =
    {
        Target: CaseSelector
        Filter: TimelineFilter option
        AfterCursor: string option
        Limit: int
    }

type CaseTimelinePage =
    {
        Disclosure: HistoryDisclosure
        AttachmentDisclosure: Sequence
        Basis: WorkDateBasis
        CaseId: CaseId
        Reference: CaseReference
        Items: CaseTimelineEntry list
        NextCursor: string option
    }
