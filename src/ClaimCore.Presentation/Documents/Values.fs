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
        Recorded: bool
    }

type Section =
    { TitleId: string; Entries: Entry list }

type Column =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        IsMoney: bool
        Recorded: bool
    }

type PresentedMessage =
    {
        MessageId: string
        Arguments: Map<string, PresentedValue>
    }

type HumanAdvice =
    {
        Message: PresentedMessage
        Action: RecommendedAction
    }

[<RequireQualifiedAccess>]
type ActionControl =
    | Operation of Binding
    | Command of CommandRequest

/// Ready selection and authored controls share one declaration-owned shape.
type Affordance =
    {
        Task: TaskId
        LabelId: string
        Control: ActionControl
        Seeds: Map<TaskInputId, InputValue>
        Editable: TaskInputId list
        Choices: Map<TaskInputId, string list>
        Blocked: HumanAdvice option
    }

    member this.Binding =
        match this.Control with
        | ActionControl.Operation binding -> Some binding
        | ActionControl.Command _ -> None

module Affordance =
    let valid value =
        let definition = TaskDeclarations.find (TaskId.token value.Task)

        let matches =
            match definition, value.Control with
            | Some definition, ActionControl.Operation binding ->
                definition.Ledger
                |> Option.exists (fun task ->
                    TaskDeclaration.bindingShape task = BindingShape.ofValue binding)
            | Some definition, ActionControl.Command command ->
                definition.Kind = ClientTaskKind.Command
                && definition.Id = CommandTaskId.key (CommandRequest.task command)
                && value.Seeds.IsEmpty
                && value.Editable.IsEmpty
                && value.Choices.IsEmpty
            | _ -> false

        match definition with
        | None -> false
        | Some declaration ->
            let scalarKeys = declaration.Inputs |> List.map _.Input |> Set.ofList

            let collections =
                declaration.Collections
                |> List.map (fun collection ->
                    TaskCollectionId.input collection.Collection, collection)
                |> Map.ofList

            let inputs = Set.union scalarKeys (collections |> Map.keys |> Set.ofSeq)

            let seedsValid =
                value.Seeds
                |> Map.forall (fun input seed ->
                    declaration.Inputs
                    |> List.tryFind (fun declared -> declared.Input = input)
                    |> Option.exists (fun declared ->
                        match seed, declared.Atom with
                        | InputValue.Null, _ -> true
                        | InputValue.Text token, TaskAtom.Text ->
                            TaskChoiceId.ofInput input
                            |> Option.forall (fun choice ->
                                List.contains token (TaskChoiceId.tokens choice))
                        | InputValue.Integer _, TaskAtom.Integer
                        | InputValue.Boolean _, TaskAtom.Boolean -> true
                        | _ -> false))

            matches
            && seedsValid
            && List.distinct value.Editable = value.Editable
            && (value.Editable |> List.forall (fun input -> Set.contains input inputs))
            && (value.Choices
                |> Map.forall (fun input choices ->
                    Set.contains input inputs
                    && List.distinct choices = choices
                    && (TaskChoiceId.ofInput input
                        |> Option.exists (fun choice ->
                            choices
                            |> List.forall (fun token ->
                                List.contains token (TaskChoiceId.tokens choice))))))

    let private validated task control seeds editable choices blocked =
        let value =
            {
                Task = task
                LabelId =
                    (if
                         TaskDeclarations.describe (TaskId.token task) |> _.Kind =
                             ClientTaskKind.Command
                     then
                         "endpoint."
                     else
                         "task.")
                    + TaskId.token task
                    + ".label"
                Control = control
                Seeds = seeds
                Editable = editable
                Choices = choices
                Blocked = blocked
            }

        if not (valid value) then
            invalidArg
                (nameof task)
                "Affordances require a matching subject and declared input controls."

        value

    let create task binding seeds editable choices blocked =
        let binding =
            binding
            |> Option.defaultWith (fun () ->
                invalidArg "binding" "An operation requires a reviewed binding.")

        validated task (ActionControl.Operation binding) seeds editable choices blocked

    let command request =
        validated
            (TaskId.ofLiteral (CommandTaskId.key (CommandRequest.task request)))
            (ActionControl.Command request)
            Map.empty
            []
            Map.empty
            None

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
    | Context
    | Facts
    | Payments
    | Work
    | NextActions
    | Notes
    | Parties
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
        Link: Link option
        Actions: Affordance list
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
        StageValue: PresentedValue option
        Meter: PresentedMoneyMeter option
        Link: Link option
        Actions: Affordance list
    }

type RegionStages =
    {
        Current: CaseStageKind
        Steps: RegionStage list
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
        Who: PresentedValue
        Summary: PresentedMessage
        Highlights: Section list
        Details: Section list
        Link: Link option
        Actions: Affordance list
    }

[<RequireQualifiedAccess>]
type RegionContent =
    | Entries of Entry list
    | Table of RegionTable
    | Stages of RegionStages
    | Codes of vocabulary: string * tokens: string list
    | Notice of RegionNotice
    | Timeline of RegionTimelineEntry list

type Region =
    {
        Role: RegionRole
        TitleId: string option
        Content: RegionContent
        Link: Link option
        Actions: Affordance list
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
    let regionAffordances (region: Region) =
        region.Actions
        @ (match region.Content with
           | RegionContent.Table table -> table.Rows |> List.collect _.Actions
           | RegionContent.Timeline entries -> entries |> List.collect _.Actions
           | RegionContent.Stages stages -> stages.Steps |> List.collect _.Actions
           | _ -> [])

    let affordances (screen: Screen) =
        screen.Regions |> List.collect regionAffordances

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
            let controls = regionAffordances region

            if not (List.forall Affordance.valid controls) then
                invalidArg (nameof regions) "A region requires valid declared input controls."

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
