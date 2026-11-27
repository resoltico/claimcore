namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type DisclosureKind =
    | OperationPreview
    | RequesterContext
    | SessionTermination
    | OperationLookup
    | OperationReceipt
    | CaseRead
    | CaseTimeline
    | RevisionConflict
    | CaseList
    | CaseSummary
    | CaseAssignees
    | CaseParties
    | CaseNotes
    | CasePayments
    | AttachmentList
    | ReminderRead
    | ReminderList
    | WorkViews
    | PeopleDirectory
    | PeopleGrants
    | PeopleHistory
    | AccessRequests
    | AttachmentDownload
    | AttachmentPreview
    | Export
    | Digest
    | TrailRead

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

[<RequireQualifiedAccess>]
type DisclosureExportQuery =
    | Cases
    | CaseSummary
    | Timeline
    | Reminders
    | PeopleHistory

module DisclosureExportQuery =
    let task =
        function
        | DisclosureExportQuery.Cases -> QueryTaskId.CaseList
        | DisclosureExportQuery.CaseSummary -> QueryTaskId.CaseSummary
        | DisclosureExportQuery.Timeline -> QueryTaskId.CaseTimeline
        | DisclosureExportQuery.Reminders -> QueryTaskId.ReminderList
        | DisclosureExportQuery.PeopleHistory -> QueryTaskId.PeopleHistory

    let vocabulary =
        Vocabulary.ofUnion<DisclosureExportQuery> "exportQueryCases" TokenCasing.CamelCase None
        |> Vocabulary.all
        |> fun cases -> Vocabulary.define "disclosureExportQuery" cases (task >> QueryTaskId.key)

    let token = Vocabulary.token vocabulary

type DisclosureExportSelection =
    {
        Filtered: bool
        Paged: bool
        Order: CaseListOrder option
        Grouping: CaseGrouping option
    }

type DisclosureExport =
    {
        Query: DisclosureExportQuery
        Selection: DisclosureExportSelection
        RowCount: int
    }

/// Evidence describes returned rows and subjects of this response only.
type DisclosurePageEvidence =
    {
        RowCount: int
        Cases: CaseId list
        People: ActorId list
    }

module DisclosurePageEvidence =
    let maximumSubjects = Paging.MaximumPageSize + 1

    let create count cases people =
        let cases = List.distinct cases
        let people = List.distinct people

        if count < 0 || cases.Length > maximumSubjects || people.Length > maximumSubjects then
            invalidArg (nameof count) "Disclosure evidence exceeds its bound."

        {
            RowCount = count
            Cases = cases
            People = people
        }

type DisclosureEvent =
    {
        ActorId: ActorId
        Kind: DisclosureKind
        CaseId: CaseId option
        AttachmentId: AttachmentId option
        Outcome: DisclosureOutcome
        Export: DisclosureExport option
        Page: DisclosurePageEvidence option
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
