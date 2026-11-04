namespace ClaimCore.Protocol.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

[<RequireQualifiedAccess>]
type PresentedValue =
    | Money of Money
    | Date of CalendarDate
    | Instant of DateTimeOffset
    | Party of PartyName
    | Text of string
    | Code of vocabulary: string * token: string
    | Actor of ActorRef
    | State of CaseHeadline
    | Count of int64
    | Reference of CaseReference
    | Identifier of string
    | Absent of reasonId: string

type Entry =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        Value: PresentedValue
    }

type Section =
    { TitleId: string; Entries: Entry list }

/// Column metadata is independent of values, so empty pages and absent facts retain stable headers.
[<NoEquality; NoComparison>]
type EntryDefinition<'reply> =
    {
        Key: string
        LabelId: string
        Prominence: Prominence
        Read: 'reply -> PresentedValue
    }

module Descriptor =
    let entries definitions value =
        definitions
        |> List.map (fun definition ->
            {
                Entry.Key = definition.Key
                LabelId = definition.LabelId
                Prominence = definition.Prominence
                Value = definition.Read value
            })

    let private absent = PresentedValue.Absent "label.notRecorded"

    let private scalar =
        function
        | CaseFieldValue.Reference value -> PresentedValue.Reference value
        | CaseFieldValue.Status value -> PresentedValue.Code("status", CaseStatus.token value)
        | CaseFieldValue.Business(Admitted.Date value) -> PresentedValue.Date value
        | CaseFieldValue.Business(Admitted.Country value) ->
            PresentedValue.Code("country", CountryCode.format value)
        | CaseFieldValue.Business(Admitted.Name value) -> PresentedValue.Party value
        | CaseFieldValue.Business(Admitted.Description value) ->
            PresentedValue.Text(IncidentDescription.format value)
        | CaseFieldValue.Business(Admitted.Amount value) -> PresentedValue.Text(Amount.format value)
        | CaseFieldValue.Business(Admitted.Currency value) ->
            PresentedValue.Code("currency", Currency.format value)

    let caseFields (declarations: FieldDescriptor list) getFields =
        let paired = declarations |> List.choose _.Pairs |> Set.ofList

        declarations
        |> List.filter (fun field -> field.Listed && not (Set.contains field.Field paired))
        |> List.map (fun field ->
            {
                EntryDefinition.Key = field.Key
                LabelId = FieldPresentation.labelId field.Field
                Prominence = field.Prominence
                Read =
                    fun reply ->
                        let values = getFields reply

                        let value field =
                            values |> Option.bind (Map.tryFind field >> Option.flatten)

                        match field.Pairs with
                        | Some currency ->
                            match value field.Field, value currency with
                            | Some(CaseFieldValue.Business(Admitted.Amount amount)),
                              Some(CaseFieldValue.Business(Admitted.Currency code)) ->
                                PresentedValue.Money(Money.create amount code)
                            | None, None -> absent
                            | _ -> invalidOp "A presented money pair must be complete."
                        | None ->
                            value field.Field |> Option.map scalar |> Option.defaultValue absent
            })

    let caseSummaryColumns =
        [
            {
                Key = "caseReference"
                LabelId = FieldPresentation.labelId BusinessField.CaseReference
                Prominence = Prominence.Primary
                Read = fun (row: CaseSummary) -> PresentedValue.Reference row.Reference
            }
            {
                Key = "headline"
                LabelId = FieldPresentation.labelId BusinessField.Status
                Prominence = Prominence.Primary
                Read = fun row -> PresentedValue.State row.Headline
            }
            yield!
                caseFields CaseFields.declarations (fun (row: CaseSummary) ->
                    CaseState.fields row.State
                    |> Option.map (fun (facts: CaseSummaryFacts) -> facts.Fields))
                |> List.map (fun definition ->
                    { definition with
                        Read =
                            fun row ->
                                if CaseState.lifecycle row.State = Lifecycle.Erased then
                                    PresentedValue.Absent "lifecycle.ERASED.label"
                                else
                                    definition.Read row
                    })
            {
                Key = "responsible"
                LabelId = FieldPresentation.labelId BusinessField.ResponsibleActor
                Prominence = Prominence.Secondary
                Read =
                    fun (row: CaseSummary) ->
                        row.Responsible
                        |> Option.map PresentedValue.Actor
                        |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label")
            }
            {
                Key = "nextAction"
                LabelId = "field.reminderAction.label"
                Prominence = Prominence.Secondary
                Read =
                    fun row ->
                        row.NextAction
                        |> Option.map (fun next ->
                            PresentedValue.Text(ReminderAction.format next.Action))
                        |> Option.defaultValue (PresentedValue.Absent "view.NO_NEXT_ACTION.label")
            }
            {
                Key = "changedAt"
                LabelId = "label.changedAt"
                Prominence = Prominence.Detail
                Read = fun row -> PresentedValue.Instant row.ChangedAt
            }
        ]

    let caseSummary row =
        [
            {
                TitleId = "subject.CASE.label"
                Entries = entries caseSummaryColumns row
            }
        ]
