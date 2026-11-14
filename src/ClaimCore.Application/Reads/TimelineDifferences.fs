namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Differences use validated retained values and retained effects, independently of current authoring policy.
module internal TimelineDifferences =
    let private fault = Error CoreFault.StoreIntegrityError

    let private change before after =
        if before = after then
            None
        else
            Some
                {
                    ValueChange.Before = before
                    After = after
                }

    let actor (references: Map<ActorId, ActorRef>) id =
        match Map.tryFind id references with
        | Some reference when reference.ActorId = id -> Ok reference
        | _ -> fault

    let private actorOption references =
        function
        | None -> Ok None
        | Some id -> actor references id |> Result.map Some

    let private money amount currency fields =
        fields
        |> Option.bind (fun fields ->
            match CaseFields.typedValue amount fields, CaseFields.typedValue currency fields with
            | Some(CaseFieldValue.Business(Admitted.Amount value)),
              Some(CaseFieldValue.Business(Admitted.Currency code)) ->
                Some(Money.create value code)
            | None, None -> None
            | _ -> invalidOp "Declared money pairs require a complete amount and currency.")

    let private caseFields before after =
        let paired = CaseFields.declarations |> List.choose _.Pairs |> Set.ofList

        CaseFields.declarations
        |> List.choose (fun field ->
            if Set.contains field.Field paired then
                None
            else
                match field.Pairs with
                | Some currency ->
                    change (money field.Field currency before) (money field.Field currency after)
                    |> Option.map (fun value ->
                        TimelineChange.CaseMoney
                            {
                                FieldName = field.Field
                                Change = value
                            })
                | None ->
                    let prior = before |> Option.bind (CaseFields.typedValue field.Field)
                    let next = after |> Option.bind (CaseFields.typedValue field.Field)

                    change prior next
                    |> Option.map (fun _ ->
                        TimelineChange.CaseField
                            {
                                FieldName = field.Field
                                Before = prior
                                After = next
                            }))

    let private caseFacts
        (_businessDate: System.DateOnly)
        (_request: CaseCommand)
        (decision: CaseDecision)
        previous
        =
        let before =
            match previous with
            | None -> Ok None
            | Some(EntryContent.Case(_, Some previous)) -> Ok(Some previous.After)
            | _ -> fault

        before
        |> Result.bind (fun before ->
            match LiveCase.ofState decision.After.State with
            | None -> fault
            | Some live ->
                let prior = before |> Option.bind (fun snapshot -> LiveCase.ofState snapshot.State)

                let fields =
                    function
                    | LiveCase.Active fields
                    | LiveCase.Voided fields -> fields

                Ok
                    [
                        yield!
                            CaseChanges.lifecycle prior live
                            |> Option.map TimelineChange.Lifecycle
                            |> Option.toList
                        yield! caseFields (prior |> Option.map fields) (Some(fields live))
                        yield!
                            CaseChanges.payments
                                decision.Payment
                                (prior |> Option.map fields)
                                (Some(fields live))
                            |> Option.map TimelineChange.Payments
                            |> Option.toList
                    ])

    let private work references (request: WorkDecisionInput) (after: CaseWorkSnapshot) previous =
        let before =
            match previous with
            | None ->
                (WorkSubject.initial after.CaseId).Snapshot
                |> Option.map Ok
                |> Option.defaultValue fault
            | Some(EntryContent.Work(_, snapshot)) -> Ok snapshot
            | _ -> fault

        before
        |> Result.bind (fun before ->
            match
                WorkSubject.applyRecorded
                    request.CaseId
                    request.ExpectedRevision
                    request.Update
                    before
                    after
            with
            | Ok after ->
                match
                    actorOption references before.Work.Responsible,
                    actorOption references after.Work.Responsible
                with
                | Ok prior, Ok next ->
                    Ok
                        [
                            yield!
                                change prior next
                                |> Option.map TimelineChange.Responsibility
                                |> Option.toList
                            yield!
                                change before.Work.Summary after.Work.Summary
                                |> Option.map TimelineChange.HandlingSummary
                                |> Option.toList
                        ]
                | _ -> fault
            | _ -> fault)

    let private reminders =
        function
        | ReminderEffects.Created value
        | ReminderEffects.Changed value -> [ value ]
        | ReminderEffects.CompletedAndPlanned(completed, next) -> [ completed; next ]

    let private reminderBefore id previous =
        match previous with
        | None -> Ok None
        | Some(EntryContent.Reminder(_, effects)) ->
            reminders effects
            |> List.tryFind (fun reminder -> Reminder.id reminder = id)
            |> Option.map Ok
            |> Option.defaultValue fault
            |> Result.map Some
        | _ -> fault

    let private reminder (request: ReminderDecisionInput) after previous =
        let id = ReminderCommand.target request.OperationId request.Command

        reminderBefore id previous
        |> Result.bind (fun before ->
            match
                ReminderSubject.applyRecorded request.OperationId before request.Command after
            with
            | Ok applied ->
                applied
                |> List.map (fun value ->
                    let prior =
                        before |> Option.filter (fun old -> Reminder.id old = Reminder.id value)

                    TimelineChange.Reminder
                        {
                            ReminderId = Reminder.id value
                            Action =
                                change
                                    (prior |> Option.map Reminder.action)
                                    (Some(Reminder.action value))
                            Due =
                                change
                                    (prior |> Option.map Reminder.due)
                                    (Some(Reminder.due value))
                            State =
                                change
                                    (prior |> Option.map Reminder.state)
                                    (Some(Reminder.state value))
                        })
                |> Ok
            | _ -> fault)

    let actorIds =
        function
        | EntryContent.Work(_, snapshot) -> snapshot.Work.Responsible |> Option.toList
        | EntryContent.Note _
        | EntryContent.Case _
        | EntryContent.People _
        | EntryContent.Reminder _
        | EntryContent.ErasedCase
        | EntryContent.ErasedProfile -> []

    let describe businessDate references current previous =
        match current with
        | EntryContent.Case(request, Some snapshot) ->
            caseFacts businessDate request snapshot previous
        | EntryContent.Work(request, snapshot) -> work references request snapshot previous
        | EntryContent.Reminder(request, effects) -> reminder request effects previous
        | EntryContent.Note(request, snapshot) ->
            let before =
                match previous with
                | None -> Ok None
                | Some(EntryContent.Note(_, before)) -> Ok(Some before)
                | _ -> fault

            match
                before
                |> Result.bind (fun before ->
                    NoteSubject.applyRecorded request.OperationId before request.Command snapshot
                    |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError))
            with
            | Ok applied ->
                Ok
                    [
                        TimelineChange.Note(
                            NoteCommand.kind request.Command,
                            Note.id applied,
                            Note.text applied
                        )
                    ]
            | Error _ -> fault
        | EntryContent.People _
        | EntryContent.Case(_, None)
        | EntryContent.ErasedCase
        | EntryContent.ErasedProfile -> fault

    /// Validate the complete accepted decision, then expose only this effect's changes.
    let describeEffect subject businessDate references current previous =
        describe businessDate references current previous
        |> Result.map (fun changes ->
            match subject with
            | EntryTarget.Reminder reminder ->
                changes
                |> List.filter (function
                    | TimelineChange.Reminder change -> change.ReminderId = reminder.ReminderId
                    | _ -> false)
            | _ -> changes)
