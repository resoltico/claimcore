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
type RegionRole =
    | Identity
    | Notice
    | Progress
    | Attention
    | Recommendation
    | Facts
    | Payments
    | Work
    | NextActions
    | Notes
    | Activity
    | Results
    | Totals
    | Account
    | Access
    | Details

module RegionRole =
    let vocabulary =
        Vocabulary.ofUnion<RegionRole> "regionRole" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type RegionContentKind =
    | Entries
    | Table
    | Stages
    | Codes
    | Actions
    | Notice
    | Timeline

module RegionContentKind =
    let vocabulary =
        Vocabulary.ofUnion<RegionContentKind> "regionContent" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type NoticeTone =
    | Info
    | Warning
    | Final

module NoticeTone =
    let vocabulary =
        Vocabulary.ofUnion<NoticeTone> "noticeTone" TokenCasing.ScreamingSnake None

type RegionRow =
    {
        Key: string
        Values: PresentedValue list
        Detail: RowDetail option
    }

type RegionRowGroup =
    {
        TitleId: string
        Count: int64
        RowKeys: string list
    }

type PresentedMoneyMeter =
    {
        Current: PresentedMoney
        Target: PresentedMoney
        Complete: bool
        Description: PresentedMessage
    }

type RegionTable =
    {
        Columns: Column list
        Rows: RegionRow list
        Groups: RegionRowGroup list
        Sections: Section list
        Meter: PresentedMoneyMeter option
    }

type RegionStage =
    {
        Stage: CaseStageKind
        Reached: bool
        Date: CalendarDate option
        Amount: PresentedMoney option
        Meter: PresentedMoneyMeter option
    }

type RegionStages =
    {
        Current: CaseStageKind
        Steps: RegionStage list
    }

type RegionActionGroup =
    {
        Family: ActFamily
        Acts: ActKind list
    }

type RegionActions =
    {
        Recommended: ActKind option
        Groups: RegionActionGroup list
    }

type RegionNotice =
    {
        MessageId: string
        Reason: PresentedValue option
        Tone: NoticeTone
    }

type RegionTimelineEntry =
    {
        Key: string
        When: DateTimeOffset
        Who: ActorRef
        Summary: PresentedMessage
        Highlights: Section list
        Details: Section list
    }

[<RequireQualifiedAccess>]
type RegionContent =
    | Entries of Entry list
    | Table of RegionTable
    | Stages of RegionStages
    | Codes of vocabulary: string * tokens: string list
    | Actions of RegionActions
    | Notice of RegionNotice
    | Timeline of RegionTimelineEntry list

type Region =
    {
        Role: RegionRole
        TitleId: string option
        Content: RegionContent
    }

/// Core-authored saved presentation for an offered correction group.
type CurrentInputGroup =
    { Group: RecordGroup; Section: Section }

type Screen =
    {
        Regions: Region list
        CurrentGroups: CurrentInputGroup list
    }

module Screen =
    let valid regions =
        regions
        |> List.filter (fun region -> region.Role <> RegionRole.Facts)
        |> List.countBy _.Role
        |> List.forall (fun (_, count) -> count = 1)

    let create regions =
        if not (valid regions) then
            invalidArg (nameof regions) "Only FACTS may repeat within a screen."

        let unique values = List.distinct values = values

        for region in regions do
            match region.Content with
            | RegionContent.Table table ->
                let keys = table.Rows |> List.map _.Key
                let grouped = table.Groups |> List.collect _.RowKeys

                if
                    not (
                        unique (table.Columns |> List.map _.Key)
                        && unique keys
                        && unique grouped
                        && (table.Rows
                            |> List.forall (fun row -> row.Values.Length = table.Columns.Length))
                        && (table.Groups
                            |> List.forall (fun group -> group.Count >= int64 group.RowKeys.Length))
                        && (table.Groups.IsEmpty || Set.ofList keys = Set.ofList grouped)
                    )
                then
                    invalidArg
                        (nameof regions)
                        "Tables require unique keys, matching widths and disjoint covering groups."
            | _ -> ()

        {
            Regions = regions
            CurrentGroups = []
        }

    let withCurrentGroups groups screen =
        let keys = groups |> List.map (fun (item: CurrentInputGroup) -> item.Group)

        if List.distinct keys <> keys || keys |> List.exists ((=) RecordGroup.Payment) then
            invalidArg
                (nameof groups)
                "Current correction groups must be unique registration or decision groups."

        { screen with CurrentGroups = groups }

type Presented<'data> = { Data: 'data; View: Screen }

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
