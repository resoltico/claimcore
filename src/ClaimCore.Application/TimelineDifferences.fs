namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Differences use validated retained values and permanent effects, independently of current authoring policy.
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

    let private caseFacts request after previous =
        let before =
            match previous with
            | None -> Ok None
            | Some(EntryContent.Case(_, Some snapshot)) -> Ok(Some snapshot)
            | _ -> fault

        before
        |> Result.bind (fun before ->
            match HistoricalCaseEffects.apply request before with
            | Ok expected when expected = after ->
                match LiveCase.ofState after.State with
                | None -> fault
                | Some live ->
                    let before =
                        before |> Option.bind (fun snapshot -> LiveCase.ofState snapshot.State)

                    let difference = CaseChanges.compared before live

                    let fields =
                        function
                        | LiveCase.Active fields
                        | LiveCase.Voided fields -> fields

                    Ok
                        [
                            yield!
                                difference.Lifecycle
                                |> Option.map TimelineChange.Lifecycle
                                |> Option.toList
                            yield! caseFields (before |> Option.map fields) (Some(fields live))
                        ]
            | _ -> fault)

    let private work references request (after: CaseWorkSnapshot) previous =
        let before =
            match previous with
            | None ->
                Ok
                    {
                        CaseWorkSnapshot.CaseId = after.CaseId
                        Revision = Revision.zero
                        Work = CaseWork.empty
                    }
            | Some(EntryContent.Work(_, snapshot)) -> Ok snapshot
            | _ -> fault

        before
        |> Result.bind (fun before ->
            match HistoricalWorkEffects.work request before with
            | Ok expected when expected = after ->
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

    let private reminder (request: ReminderRequestRecord) after previous =
        let id = ReminderCommand.target request.OperationId request.Command

        reminderBefore id previous
        |> Result.bind (fun before ->
            match HistoricalWorkEffects.reminder request before with
            | Ok expected when expected = after ->
                after
                |> reminders
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
        | EntryContent.NonAcceptance
        | EntryContent.Case _
        | EntryContent.Authority _
        | EntryContent.Reminder _
        | EntryContent.ErasedCase
        | EntryContent.ErasedProfile -> []

    let describe references current previous =
        match current with
        | EntryContent.Case(request, Some snapshot) -> caseFacts request snapshot previous
        | EntryContent.Work(request, snapshot) -> work references request snapshot previous
        | EntryContent.Reminder(request, effects) -> reminder request effects previous
        | EntryContent.NonAcceptance
        | EntryContent.Authority _
        | EntryContent.Case(_, None)
        | EntryContent.ErasedCase
        | EntryContent.ErasedProfile -> fault
