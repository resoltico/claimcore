namespace ClaimCore.Domain.Changes

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates

module PartDescriptions =
    let notes (parts: PartChange seq) =
        parts
        |> Seq.choose (fun change ->
            match change.Value, NoteId.create change.PartId with
            | PartValue.Note value, Some id -> Some(id, value)
            | _ -> None)
        |> Map.ofSeq

    let private collect results =
        results
        |> List.fold
            (fun current result ->
                current
                |> Result.bind (fun values -> result |> Result.map (fun value -> value :: values)))
            (Ok [])
        |> Result.map List.rev

    let private described (change: PartChange) description =
        {
            DescribedPart.PartId = change.PartId
            Revision = change.Revision
            Description = description
        }

    let private partyDescription (before: PartyValue option) (after: PartyValue) =
        let content (value: PartyValue) =
            if value.Removed then None else value.Content

        PartDescription.Party
            {
                Content =
                    {
                        Before = before |> Option.bind content
                        After = content after
                    }
                Removed = Difference.value (before |> Option.map _.Removed) (Some after.Removed)
            }
        |> Ok

    let family
        (before: DecisionFacts option)
        (notes: Map<NoteId, NoteValue>)
        (changes: PartChange list)
        =
        let totals content =
            content
            |> Option.bind (fun (content: CaseContent) -> FinancialPosition.totals content.Finance)

        let beforeTotals =
            before |> Option.bind (fun state -> state.Case.Value.Content) |> totals

        let afterTotals =
            changes
            |> List.tryPick (fun change ->
                match change.Value with
                | PartValue.Case value -> Some(totals value.Content)
                | _ -> None)
            |> Option.defaultValue beforeTotals

        changes
        |> List.map (fun change ->
            let prior = before

            let owner =
                match change.Aggregate with
                | AggregateReference.Family id -> Some id
                | _ -> None

            if owner.IsNone || (prior |> Option.exists (fun state -> Some state.Id <> owner)) then
                Error RecordViolation.InvalidSubjectState
            else
                let description =
                    match change.Value with
                    | PartValue.Case value ->
                        let reference =
                            prior |> Option.map _.Reference |> Option.orElse value.Reference

                        match reference with
                        | None -> Error RecordViolation.InvalidHistoryStart
                        | Some reference ->
                            let before =
                                prior
                                |> Option.map (fun state ->
                                    {
                                        CaseValue.Reference = Some reference
                                        Lifecycle = state.Case.Value.Lifecycle
                                        Status = state.Case.Value.Status
                                        Content = state.Case.Value.Content
                                        Disposition = None
                                    })

                            Ok(
                                PartDescription.Case(
                                    CaseDescription.describe reference before value
                                )
                            )
                    | PartValue.Payment value ->
                        let before =
                            OperationId.create change.PartId
                            |> Option.bind (fun id ->
                                prior |> Option.bind (fun state -> Map.tryFind id state.Payments))
                            |> Option.map _.Value

                        Ok(
                            PartDescription.Payment(
                                PaymentDescription.describe
                                    before
                                    value
                                    {
                                        Before = beforeTotals
                                        After = afterTotals
                                    }
                            )
                        )
                    | PartValue.Work value ->
                        let before = prior |> Option.bind _.Work |> Option.bind _.Value
                        Ok(PartDescription.Work(WorkDescription.describe before value))
                    | PartValue.Reminder value ->
                        let before =
                            ReminderId.create change.PartId
                            |> Option.bind (fun id ->
                                prior |> Option.bind (fun state -> Map.tryFind id state.Reminders))
                            |> Option.map (fun part -> part.Value)

                        Ok(PartDescription.Reminder(ReminderDescription.describe before value))
                    | PartValue.Note value ->
                        match NoteId.create change.PartId with
                        | None -> Error RecordViolation.InvalidSubjectState
                        | Some id ->
                            let before = Map.tryFind id notes

                            if
                                before.IsNone
                                && (prior
                                    |> Option.exists (fun state -> Map.containsKey id state.Notes))
                            then
                                Error RecordViolation.InvalidSubjectState
                            else
                                Ok(PartDescription.Note(NoteDescription.describe before value))
                    | PartValue.Party value ->
                        let prior =
                            PartyId.create change.PartId
                            |> Option.bind (fun id ->
                                prior |> Option.bind (fun state -> Map.tryFind id state.Parties))
                            |> Option.map _.Value

                        partyDescription prior value
                    | PartValue.Person _ -> Error RecordViolation.InvalidSubjectState

                description |> Result.map (described change))
        |> collect

    let person (before: Person option) (changes: PartChange list) =
        changes
        |> List.map (fun change ->
            match change.Aggregate, change.Value with
            | AggregateReference.Person id, PartValue.Person value when
                before |> Option.forall (fun state -> state.Id = id)
                ->
                PersonDescription.describe before value
                |> PartDescription.Person
                |> described change
                |> Ok
            | _ -> Error RecordViolation.InvalidSubjectState)
        |> collect

    let relatedActors descriptions =
        descriptions
        |> List.collect (fun part ->
            match part.Description with
            | PartDescription.Work value ->
                [ value.Values.Before; value.Values.After ]
                |> List.choose id
                |> List.choose _.Responsible
            | _ -> [])
        |> List.distinct

    let relatedCases descriptions =
        descriptions
        |> List.collect (fun part ->
            match part.Description with
            | PartDescription.Person value ->
                value.Grants
                |> List.choose (fun change ->
                    match change.Grant.Scope with
                    | Scope.Case id -> Some id
                    | Scope.Installation -> None)
            | _ -> [])
        |> List.distinct

    /// Historical versions use the same per-part descriptions as new decisions.
    let familyVersions
        reference
        (previous: Map<PartKind * System.Guid, PartChange>)
        (changes: PartChange list)
        =
        let prior (change: PartChange) =
            Map.tryFind (change.Kind, change.PartId) previous

        let caseTotals (change: PartChange option) =
            change
            |> Option.bind (fun change ->
                match change.Value with
                | PartValue.Case value ->
                    value.Content
                    |> Option.bind (fun content -> FinancialPosition.totals content.Finance)
                | _ -> None)

        let beforeTotals =
            previous
            |> Map.values
            |> Seq.tryFind (fun change -> change.Kind = PartKind.Case)
            |> caseTotals

        let afterTotals =
            changes
            |> List.tryFind (fun change -> change.Kind = PartKind.Case)
            |> caseTotals

        changes
        |> List.map (fun change ->
            let before = prior change

            let valid =
                match before with
                | None -> change.Revision = Revision.first
                | Some before ->
                    before.Kind = change.Kind
                    && before.PartId = change.PartId
                    && before.Aggregate = change.Aggregate
                    && PartChange.successor before.Revision change.Revision

            if not valid then
                Error RecordViolation.InvalidRevisionSequence
            else
                let description =
                    match change.Value with
                    | PartValue.Case _ ->
                        CaseDescription.fromChanges reference before change
                        |> Result.map PartDescription.Case
                    | PartValue.Payment value ->
                        let prior =
                            before
                            |> Option.bind (fun change ->
                                match change.Value with
                                | PartValue.Payment value -> Some value
                                | _ -> None)

                        Ok(
                            PartDescription.Payment(
                                PaymentDescription.describe
                                    prior
                                    value
                                    {
                                        Before = beforeTotals
                                        After = afterTotals
                                    }
                            )
                        )
                    | PartValue.Work value ->
                        let prior =
                            before
                            |> Option.bind (fun change ->
                                match change.Value with
                                | PartValue.Work value -> value
                                | _ -> None)

                        Ok(PartDescription.Work(WorkDescription.describe prior value))
                    | PartValue.Reminder value ->
                        let prior =
                            before
                            |> Option.bind (fun change ->
                                match change.Value with
                                | PartValue.Reminder value -> Some value
                                | _ -> None)

                        Ok(PartDescription.Reminder(ReminderDescription.describe prior value))
                    | PartValue.Note value ->
                        let prior =
                            before
                            |> Option.bind (fun change ->
                                match change.Value with
                                | PartValue.Note value -> Some value
                                | _ -> None)

                        Ok(PartDescription.Note(NoteDescription.describe prior value))
                    | PartValue.Party value ->
                        let prior =
                            before
                            |> Option.bind (fun change ->
                                match change.Value with
                                | PartValue.Party value -> Some value
                                | _ -> None)

                        partyDescription prior value
                    | PartValue.Person _ -> Error RecordViolation.InvalidSubjectState

                description |> Result.map (described change))
        |> collect

    let private recordedEvent kind description items =
        let generic = TimelineEvent.PartChanged kind

        match description with
        | PartDescription.Work value ->
            let responsibility =
                items
                |> List.tryPick (function
                    | TimelineChange.Responsibility value -> Some value
                    | _ -> None)

            match responsibility with
            | Some { After = Some person } -> TimelineEvent.ResponsibilityAssigned person
            | Some { Before = Some person; After = None } ->
                TimelineEvent.ResponsibilityCleared person
            | _ ->
                match value.Summary with
                | Some { Before = None; After = Some _ } -> TimelineEvent.HandlingSummaryAdded
                | Some { Before = Some _; After = Some _ } -> TimelineEvent.HandlingSummaryChanged
                | Some { Before = Some _; After = None } -> TimelineEvent.HandlingSummaryCleared
                | _ -> TimelineEvent.WorkRecorded
        | PartDescription.Reminder value ->
            match
                value.State |> Option.map (fun change -> change.Before, change.After),
                value.Plans.After
            with
            | Some(None, Some ReminderState.Pending), Some plan ->
                TimelineEvent.ReminderPlanned(plan.Action, plan.Due)
            | Some(Some ReminderState.Pending, Some ReminderState.Completed), Some plan ->
                TimelineEvent.ReminderCompleted plan.Action
            | Some(Some ReminderState.Pending, Some ReminderState.Cancelled), Some plan ->
                TimelineEvent.ReminderCancelled plan.Action
            | Some(Some(ReminderState.Completed | ReminderState.Cancelled),
                   Some ReminderState.Pending),
              Some plan -> TimelineEvent.ReminderReopened(plan.Action, plan.Due)
            | None, Some plan when value.Action.IsSome || value.Due.IsSome ->
                TimelineEvent.ReminderChanged plan.Action
            | _ -> generic
        | PartDescription.Note value ->
            match value.Removed |> Option.bind _.After with
            | Some removed -> TimelineEvent.NoteRecorded removed
            | None -> generic
        | _ -> generic

    /// Domain values are adapted to the existing typed timeline grammar, never re-diffed.
    let timeline (actors: Map<ActorId, ActorRef>) (part: DescribedPart) =
        let missing = Error RecordViolation.InvalidSubjectState

        let reference =
            function
            | None -> Ok None
            | Some id ->
                Map.tryFind id actors
                |> Option.filter (fun actor -> actor.ActorId = id)
                |> Option.map (Some >> Ok)
                |> Option.defaultValue missing

        let kind, items =
            match part.Description with
            | PartDescription.Case value ->
                let lifecycle =
                    value.Lifecycle
                    |> Option.bind (fun change ->
                        change.After
                        |> Option.map (fun after ->
                            TimelineChange.Lifecycle
                                {
                                    Before = change.Before
                                    After = after
                                }))
                    |> Option.toList

                PartKind.Case,
                Ok(
                    lifecycle
                    @ (value.Disposition |> Option.map TimelineChange.Disposition |> Option.toList)
                    @ (value.Fields |> List.map TimelineChange.CaseField)
                    @ (value.Money |> List.map TimelineChange.CaseMoney)
                )
            | PartDescription.Payment value ->
                let payment =
                    value.Entry
                    |> Option.map (fun entry ->
                        if value.Removed |> Option.bind _.After = Some true then
                            PaymentDifference.Removed entry
                        else
                            PaymentDifference.Recorded entry)

                let items =
                    match payment with
                    | None -> Ok []
                    | Some payment ->
                        PaymentCollectionDifference.create
                            [ payment ]
                            value.Totals.Before
                            value.Totals.After
                        |> Result.map (TimelineChange.Payments >> List.singleton)
                        |> Result.mapError (fun _ -> RecordViolation.InvalidPaymentState)

                PartKind.Payment, items
            | PartDescription.Work value ->
                let responsibility =
                    match value.Responsible with
                    | None -> Ok []
                    | Some change ->
                        reference change.Before
                        |> Result.bind (fun before ->
                            reference change.After
                            |> Result.map (fun after ->
                                [
                                    TimelineChange.Responsibility
                                        { Before = before; After = after }
                                ]))

                PartKind.Work,
                responsibility
                |> Result.map (fun changes ->
                    changes
                    @ (value.Summary |> Option.map TimelineChange.HandlingSummary |> Option.toList))
            | PartDescription.Reminder value ->
                let items =
                    ReminderId.create part.PartId
                    |> Option.map (fun id ->
                        [
                            TimelineChange.Reminder
                                {
                                    ReminderId = id
                                    Action = value.Action
                                    Due = value.Due
                                    State = value.State
                                }
                        ])
                    |> Option.map Ok
                    |> Option.defaultValue missing

                PartKind.Reminder, items
            | PartDescription.Note value ->
                let items =
                    NoteId.create part.PartId
                    |> Option.map (fun id ->
                        let removed = value.Removed |> Option.bind _.After = Some true

                        let text =
                            value.Text
                            |> Option.bind (fun change ->
                                if removed then change.Before else change.After)

                        [ TimelineChange.Note(removed, id, text) ])
                    |> Option.map Ok
                    |> Option.defaultValue missing

                PartKind.Note, items
            | PartDescription.Party value ->
                let items =
                    PartyId.create part.PartId
                    |> Option.map (fun id -> [ TimelineChange.Party(id, value.Content) ])
                    |> Option.map Ok
                    |> Option.defaultValue missing

                PartKind.Party, items
            | PartDescription.Person _ -> PartKind.Person, missing

        items
        |> Result.bind (fun items ->
            let event = recordedEvent kind part.Description items

            let highlights =
                items
                |> List.mapi (fun index change ->
                    match change with
                    | TimelineChange.Responsibility _ when
                        (match event with
                         | TimelineEvent.ResponsibilityAssigned _
                         | TimelineEvent.ResponsibilityCleared _ -> true
                         | _ -> false)
                        ->
                        None
                    | TimelineChange.Reminder _ when
                        (match event with
                         | TimelineEvent.ReminderPlanned _ -> true
                         | _ -> false)
                        ->
                        None
                    | TimelineChange.Reminder value ->
                        let stateInEvent =
                            match event with
                            | TimelineEvent.ReminderCompleted _
                            | TimelineEvent.ReminderCancelled _
                            | TimelineEvent.ReminderReopened _ -> true
                            | _ -> false

                        let facets =
                            [
                                if value.Action.IsSome then
                                    TimelineFacet.Action

                                if value.Due.IsSome then
                                    TimelineFacet.Due

                                if value.State.IsSome && not stateInEvent then
                                    TimelineFacet.State
                            ]

                        if facets.IsEmpty then
                            None
                        else
                            Some(TimelineHighlight.Reminder(index, facets))
                    | _ -> Some(TimelineHighlight.Change index))
                |> List.choose id

            TimelineDisclosure.create
                items
                {
                    Event = event
                    Highlights = highlights
                }
            |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState))
