namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module TimelineCodecs =
    open Codec
    open ResponseScalars

    let private valueChange name value =
        (record (fun before after ->
            {
                ValueChange.Before = before
                After = after
            })
        : Fields<ValueChange<_>, _>)
        |> field "before" _.Before (nullable value)
        |> field "after" _.After (nullable value)
        |> build
        |> named name

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
        |> field "action" _.Action (nullable (valueChange "ReminderActionChange" reminderAction))
        |> field "due" _.Due (nullable (valueChange "ReminderDueChange" calendarDate))
        |> field
            "state"
            _.State
            (nullable (
                valueChange "ReminderStateChange" (VocabularyCodec.create ReminderState.vocabulary)
            ))
        |> build
        |> named "ReminderDifference"

    let private fieldDifference =
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

    let private moneyDifference =
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
        |> field "change" _.Change (valueChange "MoneyChange" ResponseScalars.money)
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
                     |> field "change" id (valueChange "ResponsibilityChange" ActorCodecs.actorRef))
                variant
                    "HANDLING_SUMMARY"
                    TimelineChange.HandlingSummary
                    (function
                    | TimelineChange.HandlingSummary value -> Some value
                    | _ -> None)
                    (record id
                     |> field "change" id (valueChange "HandlingSummaryChange" handlingSummary))
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
                for act in CaseActKind.all do
                    yield
                        unitEvent
                            (TimelineEventPresentation.caseKind act |> fst)
                            (TimelineEvent.CaseActRecorded act)
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

        constrained
            codec.Schema
            (function
            | TimelineChanges.Disclosed value -> TimelineDisclosure.resolve value |> Result.isOk
            | _ -> true)
            codec

    let private entry operation position at author changes subject =
        {
            CaseTimelineEntry.OperationId = operation
            Position = position
            Subject = subject
            RecordedAt = at
            RecordedBy = author
            Changes = changes
        }

    let private metadata (fields: Fields<CaseTimelineEntry, _>) =
        fields
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position LedgerCodecs.position
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "changes" _.Changes changes

    let private caseParts (entry: CaseTimelineEntry) =
        match entry.Subject with
        | TimelineSubject.CaseFact(revision, act) -> revision, act
        | _ -> invalidArg (nameof entry) "Use a case-fact timeline entry."

    let private workParts (entry: CaseTimelineEntry) =
        match entry.Subject with
        | TimelineSubject.Work(revision, act) -> revision, act
        | _ -> invalidArg (nameof entry) "Use a work timeline entry."

    let private reminderParts (entry: CaseTimelineEntry) =
        match entry.Subject with
        | TimelineSubject.Reminder(act, id, revision, next) -> act, id, revision, next
        | _ -> invalidArg (nameof entry) "Use a reminder timeline entry."

    let private caseFields =
        (record (fun operation position at author changes revision act ->
            entry operation position at author changes (TimelineSubject.CaseFact(revision, act)))
        : Fields<CaseTimelineEntry, _>)
        |> metadata
        |> field "revision" (caseParts >> fst) positiveRevision
        |> field "act" (caseParts >> snd) (VocabularyCodec.create CaseActKind.vocabulary)

    let private workFields =
        (record (fun operation position at author changes revision act ->
            entry operation position at author changes (TimelineSubject.Work(revision, act)))
        : Fields<CaseTimelineEntry, _>)
        |> metadata
        |> field "revision" (workParts >> fst) positiveRevision
        |> field "act" (workParts >> snd) (VocabularyCodec.create WorkActKind.vocabulary)

    let private reminderVersion =
        record (fun id revision -> id, revision)
        |> field "reminderId" fst reminderId
        |> field "revision" snd positiveRevision
        |> build
        |> named "TimelineReminderVersion"

    let private reminderFields =
        (record (fun operation position at author changes act id revision next ->
            entry
                operation
                position
                at
                author
                changes
                (TimelineSubject.Reminder(act, id, revision, next)))
        : Fields<CaseTimelineEntry, _>)
        |> metadata
        |> field
            "act"
            (fun value -> let (act, _, _, _) = reminderParts value in act)
            (VocabularyCodec.create ReminderActKind.vocabulary)
        |> field
            "reminderId"
            (fun value -> let (_, id, _, _) = reminderParts value in id)
            reminderId
        |> field
            "revision"
            (fun value -> let (_, _, revision, _) = reminderParts value in revision)
            positiveRevision
        |> field
            "next"
            (fun value -> let (_, _, _, next) = reminderParts value in next)
            (nullable reminderVersion)

    let timelineEntry =
        tagged
            "CaseTimelineEntry"
            [
                variant
                    "CASE_FACT"
                    id
                    (fun (entry: CaseTimelineEntry) ->
                        match entry.Subject with
                        | TimelineSubject.CaseFact _ -> Some entry
                        | _ -> None)
                    caseFields
                variant
                    "WORK"
                    id
                    (fun (entry: CaseTimelineEntry) ->
                        match entry.Subject with
                        | TimelineSubject.Work _ -> Some entry
                        | _ -> None)
                    workFields
                variant
                    "REMINDER"
                    id
                    (fun (entry: CaseTimelineEntry) ->
                        match entry.Subject with
                        | TimelineSubject.Reminder _ -> Some entry
                        | _ -> None)
                    reminderFields
            ]

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
