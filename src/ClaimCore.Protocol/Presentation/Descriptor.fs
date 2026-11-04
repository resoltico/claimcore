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

    let private fields (declarations: FieldDescriptor list) getFields =
        let paired = declarations |> List.choose _.Pairs |> Set.ofList

        declarations
        |> List.filter (fun field -> not (Set.contains field.Field paired))
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

    let caseFields declarations getFields =
        fields (declarations |> List.filter _.Listed) getFields

    let private value key label prominence value =
        {
            Entry.Key = key
            LabelId = label
            Prominence = prominence
            Value = value
        }

    let private optional format =
        Option.map format >> Option.defaultValue absent

    /// A full card uses all declared facts, including non-listed fields; complete money pairs stay atomic.
    let caseFacts (snapshot: CaseSnapshot) =
        match CaseState.fields snapshot.State with
        | None -> []
        | Some facts ->
            let values =
                CaseFields.declarations
                |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field facts)
                |> Map.ofList

            RecordGroup.vocabulary
            |> Vocabulary.all
            |> List.map (fun group ->
                let declarations =
                    CaseFields.declarations |> List.filter (fun field -> field.Group = Some group)

                {
                    TitleId = Vocabulary.messageKey RecordGroup.vocabulary group "label"
                    Entries = entries (fields declarations (fun () -> Some values)) ()
                })

    let caseWork (work: CaseWorkView) =
        {
            TitleId = "section.work.label"
            Entries =
                [
                    value
                        "responsible"
                        "field.responsibleActor.label"
                        Prominence.Primary
                        (work.Responsible
                         |> Option.map PresentedValue.Actor
                         |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label"))
                    value
                        "handlingSummary"
                        "field.handlingSummary.label"
                        Prominence.Secondary
                        (optional (HandlingSummary.format >> PresentedValue.Text) work.Summary)
                    value
                        "nextAction"
                        "field.reminderAction.label"
                        Prominence.Primary
                        (optional (ReminderAction.format >> PresentedValue.Text) work.NextAction)
                    value
                        "followUp"
                        "label.nextFollowUp"
                        Prominence.Secondary
                        (optional PresentedValue.Date work.FollowUp)
                    value
                        "pendingReminders"
                        "label.pendingReminders"
                        Prominence.Secondary
                        (PresentedValue.Count work.PendingReminders)
                    value
                        "overdueReminders"
                        "label.overdueReminders"
                        Prominence.Secondary
                        (PresentedValue.Count work.OverdueReminders)
                    value
                        "workRevision"
                        "label.workRevision"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format work.Revision))
                    value
                        "workChangedAt"
                        "label.workChangedAt"
                        Prominence.Detail
                        (optional PresentedValue.Instant work.ChangedAt)
                    value
                        "workRecordedBy"
                        "label.workRecordedBy"
                        Prominence.Detail
                        (optional PresentedValue.Actor work.RecordedBy)
                ]
        }

    let caseView (reply: CaseReadModel) =
        [
            yield
                {
                    TitleId = "subject.CASE.label"
                    Entries =
                        [
                            value
                                "caseReference"
                                "field.caseReference.label"
                                Prominence.Primary
                                (PresentedValue.Reference reply.Snapshot.Reference)
                            value
                                "headline"
                                "field.status.label"
                                Prominence.Primary
                                (PresentedValue.State reply.Headline)
                            value
                                "revision"
                                "label.revisionLabel"
                                Prominence.Detail
                                (PresentedValue.Identifier(Revision.format reply.Snapshot.Revision))
                        ]
                }
            yield! caseFacts reply.Snapshot
            if CaseState.lifecycle reply.Snapshot.State <> Lifecycle.Erased then
                yield caseWork reply.Work
        ]

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

    let private count key label value =
        {
            Entry.Key = key
            LabelId = label
            Prominence = Prominence.Primary
            Value = PresentedValue.Count value
        }

    /// Counts retain known zero values and the producer's order; absence is never inferred.
    let caseViews (reply: CaseViews) =
        reply.Items
        |> List.map (fun item ->
            let token = CaseView.token item.View
            count token ("view." + token + ".label") item.Count)

    let reminderViews (reply: ReminderViews) =
        reply.Items
        |> List.map (fun item ->
            let token = ReminderView.token item.View
            count token ("view." + token + ".label") item.Count)

    let workOverview (reply: WorkOverview) =
        [
            count "actionableCases" "label.actionableCases" reply.ActionableCases
            count "missingPlanCases" "label.missingPlanCases" reply.MissingPlanCases
            count "overdueCases" "label.overdueCases" reply.OverdueCases
            count "overdueReminders" "label.overdueReminders" reply.OverdueReminders
            count "unassignedCases" "label.unassignedCases" reply.UnassignedCases
            count
                "unavailableAssigneeCases"
                "label.unavailableAssigneeCases"
                reply.UnavailableAssigneeCases
            count "attentionCases" "label.attentionCases" reply.AttentionCases
        ]
