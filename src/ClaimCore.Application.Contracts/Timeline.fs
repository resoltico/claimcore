namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type TimelineFilter =
    | Facts
    | Work
    | Notes

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TimelineFilter =
    let vocabulary =
        Vocabulary.ofUnion<TimelineFilter> "timelineFilter" TokenCasing.ScreamingSnake None

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
        Basis: WorkDateBasis
        CaseId: CaseId
        Reference: CaseReference
        Items: CaseTimelineEntry list
        NextCursor: string option
    }
