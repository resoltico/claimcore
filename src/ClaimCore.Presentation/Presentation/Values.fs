namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation

type PresentedMoney = { Value: Money; DisplayScale: int }

type PresentedMoneyTotal =
    {
        Currency: Currency
        Value: DecimalTotal
        DisplayScale: int
    }

[<RequireQualifiedAccess>]
type PresentedValue =
    | Money of PresentedMoney
    | MoneyTotal of PresentedMoneyTotal
    | Date of CalendarDate
    | Instant of DateTimeOffset
    | Party of PartyName
    | Text of string
    | Narrative of string
    | Message of messageId: string
    | Code of vocabulary: string * token: string
    | Codes of vocabulary: string * tokens: string list
    | Actor of ActorRef
    | State of CaseHeadline
    | Count of int64
    | Reference of CaseReference
    | Identifier of string
    | Absent of reasonId: string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedValue =
    let money value =
        PresentedValue.Money
            {
                Value = value
                DisplayScale = Money.displayScale value
            }

    let moneyTotal currency value =
        PresentedValue.MoneyTotal
            {
                Currency = currency
                Value = value
                DisplayScale = DecimalTotal.displayScale currency value
            }

type Entry =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        Value: PresentedValue
    }

type Section =
    { TitleId: string; Entries: Entry list }

type Column =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        IsMoney: bool
    }

type PresentedMessage =
    {
        MessageId: string
        Arguments: Map<string, PresentedValue>
    }

type RowDetail =
    {
        Summary: PresentedMessage option
        Highlights: Section list
        Sections: Section list
    }

[<RequireQualifiedAccess>]
type View =
    | Record of Section list
    | Table of
        columns: Column list *
        rows: PresentedValue list list *
        details: RowDetail option list *
        sections: Section list

type Presented<'data> = { Data: 'data; View: View }

module Presented =
    let create describe data = { Data = data; View = describe data }

    let outcome describe =
        Outcome.fold (create describe >> Outcome.Succeeded) Outcome.Rejected Outcome.Failed

/// Column metadata is independent of values, so empty pages and absent facts retain stable headers.
[<NoEquality; NoComparison>]
type EntryDefinition<'reply> =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        IsMoney: bool
        Read: 'reply -> PresentedValue
    }

type PresentedChange =
    {
        Key: string
        LabelId: string
        Before: PresentedValue
        After: PresentedValue
    }

type ReviewDescription =
    {
        Changes: PresentedChange list
        Unchanged: Entry list
        Effects: Section list
        Basis: WorkDateBasis
    }

[<RequireQualifiedAccess>]
type PresentedBoolean =
    | False
    | True

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedBoolean =
    let vocabulary =
        Vocabulary.ofUnion<PresentedBoolean> "boolean" TokenCasing.ScreamingSnake None

    let ofBool value =
        if value then
            PresentedBoolean.True
        else
            PresentedBoolean.False

type PresentedField = { Key: string; LabelId: string }

type ReviewPresentation =
    {
        Changes: PresentedChange list
        Unchanged: PresentedField list
    }

namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation

type PresentedCaseReview =
    {
        Before: CaseSnapshot option
        Proposed: CaseSnapshot
        Context: RuntimeContext
    }

[<RequireQualifiedAccess>]
type PresentedReviewSubject =
    | Case of PresentedCaseReview
    | Authority of AuthorityReview
    | Work of WorkReview
    | Reminder of ReminderReview

type PresentedReview =
    {
        Subject: PresentedReviewSubject
        Presentation: ReviewPresentation
        View: View
    }

[<RequireQualifiedAccess>]
type PresentedPreviewed =
    | Reviewed of PresentedReview
    | AlreadyAccepted of Presented<OperationReceipt>

type PresentedPreviewReply =
    {
        OperationId: Guid
        Outcome: Outcome<PresentedPreviewed>
    }

[<RequireQualifiedAccess>]
type PresentedExecuteResult =
    | Accepted of Presented<OperationReceipt>
    | AlreadyAccepted of Presented<OperationReceipt>
    | Rejected of Refusal
    | Failed of CoreFault
    | OutcomeUnknown

type PresentedExecuteReply =
    {
        OperationId: Guid
        Outcome: PresentedExecuteResult
    }

[<RequireQualifiedAccess>]
type PresentedLookupResult =
    | Accepted of Presented<OperationReceipt> * byRequester: bool
    | Pending of finalAt: DateTimeOffset
    | NotAccepted
