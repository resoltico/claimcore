namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module TimelineCodecs =
    open Codec
    open ResponseScalars

    let private reminderDifference =
        (record (fun id action due state ->
            {
                ReminderDifference.ReminderId = id
                Action = action
                Due = due
                State = state
            })
        : Fields<ReminderDifference, _>)
        |> field "reminderId" _.ReminderId reminderId
        |> field
            "action"
            _.Action
            (nullable (ValueChangeCodecs.change "ReminderActionChange" reminderAction))
        |> field "due" _.Due (nullable (ValueChangeCodecs.change "ReminderDueChange" calendarDate))
        |> field
            "state"
            _.State
            (nullable (
                ValueChangeCodecs.change
                    "ReminderStateChange"
                    (VocabularyCodec.create ReminderState.vocabulary)
            ))
        |> build
        |> named "ReminderDifference"

    let internal fieldDifference =
        (record (fun field before after ->
            {
                CaseFieldDifference.FieldName = field
                Before = before
                After = after
            })
        : Fields<CaseFieldDifference, _>)
        |> field "field" _.FieldName (VocabularyCodec.create BusinessField.vocabulary)
        |> field "before" _.Before (nullable CaseFieldCodecs.value)
        |> field "after" _.After (nullable CaseFieldCodecs.value)
        |> build
        |> named "CaseFieldDifference"

    let internal moneyDifference =
        (record (fun field change ->
            {
                CaseMoneyDifference.FieldName = field
                Change = change
            })
        : Fields<CaseMoneyDifference, _>)
        |> field
            "field"
            _.FieldName
            (VocabularyCodec.select
                (fun field ->
                    (BusinessField.describe field).Kind = FieldValueKind.Amount
                    && (BusinessField.describe field).Pairs.IsSome)
                BusinessField.vocabulary)
        |> field "change" _.Change (ValueChangeCodecs.change "MoneyChange" ResponseScalars.money)
        |> build
        |> named "CaseMoneyDifference"

    let private change =
        tagged
            "TimelineChange"
            [
                variant
                    "PAYMENTS"
                    TimelineChange.Payments
                    (function
                    | TimelineChange.Payments value -> Some value
                    | _ -> None)
                    (record id |> field "change" id PaymentCodecs.changes)
                variant
                    "CASE_FIELD"
                    TimelineChange.CaseField
                    (function
                    | TimelineChange.CaseField value -> Some value
                    | _ -> None)
                    (record id |> field "change" id fieldDifference)
                variant
                    "CASE_MONEY"
                    TimelineChange.CaseMoney
                    (function
                    | TimelineChange.CaseMoney value -> Some value
                    | _ -> None)
                    (record id |> field "change" id moneyDifference)
                variant
                    "LIFECYCLE"
                    TimelineChange.Lifecycle
                    (function
                    | TimelineChange.Lifecycle value -> Some value
                    | _ -> None)
                    (record id |> field "change" id LedgerCodecs.lifecycleChange)
                variant
                    "RESPONSIBILITY"
                    TimelineChange.Responsibility
                    (function
                    | TimelineChange.Responsibility value -> Some value
                    | _ -> None)
                    (record id
                     |> field
                         "change"
                         id
                         (ValueChangeCodecs.change "ResponsibilityChange" ActorCodecs.actorRef))
                variant
                    "HANDLING_SUMMARY"
                    TimelineChange.HandlingSummary
                    (function
                    | TimelineChange.HandlingSummary value -> Some value
                    | _ -> None)
                    (record id
                     |> field
                         "change"
                         id
                         (ValueChangeCodecs.change "HandlingSummaryChange" handlingSummary))
                variant
                    "NOTE"
                    (fun (act, id, text) -> TimelineChange.Note(act, id, text))
                    (function
                    | TimelineChange.Note(act, id, text) -> Some(act, id, text)
                    | _ -> None)
                    (record (fun act id text -> act, id, text)
                     |> field "removed" (fun (removed, _, _) -> removed) boolean
                     |> field "noteId" (fun (_, id, _) -> id) noteId
                     |> field "text" (fun (_, _, text) -> text) (nullable noteText))
                variant
                    "REMINDER"
                    TimelineChange.Reminder
                    (function
                    | TimelineChange.Reminder value -> Some value
                    | _ -> None)
                    (record id |> field "change" id reminderDifference)
            ]

    let private unitEvent kind value =
        variant
            kind
            (fun () -> value)
            (fun event -> if event = value then Some() else None)
            (record ()
             |> fixedField "messageId" (TimelineEventPresentation.describe value).MessageId)

    let private personEvent kind create project =
        variant
            kind
            create
            project
            (record id
             |> fixedField
                 "messageId"
                 ("timeline.event."
                  + (if kind = "RESPONSIBILITY_ASSIGNED" then
                         "responsibilityAssigned"
                     else
                         "responsibilityCleared"))
             |> field "person" id ActorCodecs.actorRef)

    let private actionEvent kind key create project =
        variant
            kind
            create
            project
            (record id
             |> fixedField "messageId" ("timeline.event." + key)
             |> field "action" id reminderAction)

    let private plannedEvent kind key create project =
        variant
            kind
            create
            project
            (record (fun action due -> action, due)
             |> fixedField "messageId" ("timeline.event." + key)
             |> field "action" fst reminderAction
             |> field "due" snd calendarDate)

    let private reminderEvents =
        [
            yield
                plannedEvent
                    "REMINDER_PLANNED"
                    "reminderPlanned"
                    TimelineEvent.ReminderPlanned
                    (function
                     | TimelineEvent.ReminderPlanned(action, due) -> Some(action, due)
                     | _ -> None)
            yield
                actionEvent
                    "REMINDER_CHANGED"
                    "reminderChanged"
                    TimelineEvent.ReminderChanged
                    (function
                     | TimelineEvent.ReminderChanged value -> Some value
                     | _ -> None)
            yield
                actionEvent
                    "REMINDER_COMPLETED"
                    "reminderCompleted"
                    TimelineEvent.ReminderCompleted
                    (function
                     | TimelineEvent.ReminderCompleted value -> Some value
                     | _ -> None)
            yield
                actionEvent
                    "REMINDER_CANCELLED"
                    "reminderCancelled"
                    TimelineEvent.ReminderCancelled
                    (function
                     | TimelineEvent.ReminderCancelled value -> Some value
                     | _ -> None)
            yield
                plannedEvent
                    "REMINDER_REOPENED"
                    "reminderReopened"
                    TimelineEvent.ReminderReopened
                    (function
                     | TimelineEvent.ReminderReopened(action, due) -> Some(action, due)
                     | _ -> None)
            yield
                variant
                    "REMINDER_COMPLETED_AND_PLANNED"
                    TimelineEvent.ReminderCompletedAndPlanned
                    (function
                    | TimelineEvent.ReminderCompletedAndPlanned(completed, next, due) ->
                        Some(completed, next, due)
                    | _ -> None)
                    (record (fun completed next due -> completed, next, due)
                     |> fixedField "messageId" "timeline.event.reminderCompletedAndPlanned"
                     |> field "completedAction" (fun (value, _, _) -> value) reminderAction
                     |> field "nextAction" (fun (_, value, _) -> value) reminderAction
                     |> field "nextDue" (fun (_, _, value) -> value) calendarDate)
        ]

    let private event =
        tagged
            "TimelineEvent"
            [
                for part in Vocabulary.all PartKind.vocabulary do
                    yield
                        unitEvent
                            ("PART_" + PartKind.token part + "_RECORDED")
                            (TimelineEvent.PartChanged part)
                yield
                    personEvent
                        "RESPONSIBILITY_ASSIGNED"
                        TimelineEvent.ResponsibilityAssigned
                        (function
                         | TimelineEvent.ResponsibilityAssigned value -> Some value
                         | _ -> None)
                yield
                    personEvent
                        "RESPONSIBILITY_CLEARED"
                        TimelineEvent.ResponsibilityCleared
                        (function
                         | TimelineEvent.ResponsibilityCleared value -> Some value
                         | _ -> None)
                yield unitEvent "NOTE_ADDED" (TimelineEvent.NoteRecorded false)
                yield unitEvent "NOTE_REMOVED" (TimelineEvent.NoteRecorded true)
                yield unitEvent "WORK_RECORDED" TimelineEvent.WorkRecorded
                yield unitEvent "HANDLING_SUMMARY_ADDED" TimelineEvent.HandlingSummaryAdded
                yield unitEvent "HANDLING_SUMMARY_CHANGED" TimelineEvent.HandlingSummaryChanged
                yield unitEvent "HANDLING_SUMMARY_CLEARED" TimelineEvent.HandlingSummaryCleared
                yield! reminderEvents
            ]

    let private highlight =
        let index = integer 0L (int64 CaseFields.declarations.Length) |> map int int64

        let facets =
            VocabularyCodec.create TimelineFacet.vocabulary |> named "TimelineFacet"

        tagged
            "TimelineHighlight"
            [
                variant
                    "CHANGE"
                    TimelineHighlight.Change
                    (function
                    | TimelineHighlight.Change index -> Some index
                    | _ -> None)
                    (record id |> field "index" id index)
                variant
                    "REMINDER"
                    TimelineHighlight.Reminder
                    (function
                    | TimelineHighlight.Reminder(index, facets) -> Some(index, facets)
                    | _ -> None)
                    (record (fun index facets -> index, facets)
                     |> field "index" fst index
                     |> field "facets" snd (boundedList 1 3 facets))
            ]

    let private overview =
        (record (fun event highlights ->
            {
                TimelineOverview.Event = event
                Highlights = highlights
            })
        : Fields<TimelineOverview, _>)
        |> field "event" _.Event event
        |> field "highlights" _.Highlights (list (CaseFields.declarations.Length + 1) highlight)
        |> build
        |> named "TimelineOverview"

    // Structural references are checked without constructing financial or business state.
    let private highlightStructure path value =
        let memberArray name value =
            match JsonValue.tryMember name value with
            | Some(JsonValue.Array values) -> values
            | _ -> []

        let kind value = JsonValue.tryMember "kind" value

        if kind value <> Some(JsonValue.String "DISCLOSED") then
            Ok()
        else
            let items = memberArray "items" value

            let highlights =
                JsonValue.tryMember "overview" value
                |> Option.map (memberArray "highlights")
                |> Option.defaultValue []

            let facet facet item =
                let name, rank =
                    match facet with
                    | JsonValue.String "ACTION" -> "action", 0
                    | JsonValue.String "DUE" -> "due", 1
                    | JsonValue.String "STATE" -> "state", 2
                    | _ -> "", -1

                let available =
                    JsonValue.tryMember "change" item
                    |> Option.bind (JsonValue.tryMember name)
                    |> Option.exists ((<>) JsonValue.Null)

                rank, available

            highlights
            |> List.indexed
            |> List.fold
                (fun result (ordinal, reference) ->
                    result
                    |> Result.bind (fun previous ->
                        let invalid () =
                            Error
                                {
                                    Path = sprintf "%s.overview.highlights[%d]" path ordinal
                                    Reason = "Invalid disclosed highlight reference."
                                }

                        match JsonValue.tryMember "index" reference with
                        | Some(JsonValue.Integer index) when
                            index > previous && index < int64 items.Length
                            ->
                            let item = List.item (int index) items

                            match kind reference, kind item with
                            | Some(JsonValue.String "CHANGE"), itemKind when
                                itemKind <> Some(JsonValue.String "REMINDER")
                                ->
                                Ok index
                            | Some(JsonValue.String "REMINDER"), Some(JsonValue.String "REMINDER") ->
                                let facets = memberArray "facets" reference
                                let ranks = facets |> List.map (fun value -> facet value item)

                                if
                                    not ranks.IsEmpty
                                    && ranks |> List.forall snd
                                    && (ranks
                                        |> List.map fst
                                        |> List.pairwise
                                        |> List.forall (fun (first, next) -> first < next))
                                then
                                    Ok index
                                else
                                    invalid ()
                            | _ -> invalid ()
                        | _ -> invalid ()))
                (Ok -1L)
            |> Result.map ignore

    let private changes =
        let codec =
            tagged
                "TimelineChanges"
                [
                    variant
                        "DISCLOSED"
                        TimelineChanges.Disclosed
                        (function
                        | TimelineChanges.Disclosed value -> Some value
                        | _ -> None)
                        ((record (fun items overview ->
                            {
                                TimelineDisclosure.Items = items
                                Overview = overview
                            })
                         : Fields<TimelineDisclosure, _>)
                         |> field "items" _.Items (list (CaseFields.declarations.Length + 1) change)
                         |> field "overview" _.Overview overview)
                    unitVariant "WITHHELD" TimelineChanges.Withheld
                    unitVariant "ERASED" TimelineChanges.Erased
                ]

        codec
        |> restrict codec.Schema highlightStructure
        |> refine (function
            | TimelineChanges.Disclosed value -> TimelineDisclosure.resolve value |> Result.isOk
            | _ -> true)

    let private subject =
        (record (fun kind id revision ->
            {
                TimelineSubject.PartKind = kind
                PartId = id
                Revision = revision
            })
        : Fields<TimelineSubject, _>)
        |> field "partKind" _.PartKind (VocabularyCodec.create PartKind.vocabulary)
        |> field "partId" _.PartId uuid
        |> field "revision" _.Revision positiveRevision
        |> build
        |> named "TimelineSubject"

    let timelineEntry =
        (record (fun operation position hash at author task subject changes ->
            {
                CaseTimelineEntry.OperationId = operation
                Position = position
                OperationHash = hash
                RecordedAt = at
                RecordedBy = author
                Task = task
                Subject = subject
                Changes = changes
            })
        : Fields<CaseTimelineEntry, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position LedgerCodecs.effectPosition
        |> field "operationHash" _.OperationHash LedgerCodecs.hash
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "task" _.Task TaskVocabulary.codec
        |> field "subject" _.Subject subject
        |> field "changes" _.Changes changes
        |> build
        |> named "CaseTimelineEntry"

    let disclosure =
        VocabularyCodec.create HistoryDisclosure.vocabulary |> named "HistoryDisclosure"

    let page =
        (record (fun disclosure basis parent reference items next ->
            {
                CaseTimelinePage.Disclosure = disclosure
                Basis = basis
                CaseId = parent
                Reference = reference
                Items = items
                NextCursor = next
            })
        : Fields<CaseTimelinePage, _>)
        |> field "disclosure" _.Disclosure disclosure
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "items" _.Items (list Paging.MaximumTimelinePageSize timelineEntry)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "CaseTimelinePage"
