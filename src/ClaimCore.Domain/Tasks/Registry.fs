namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain

/// This list is the single inventory of ledgered tasks, including the operator-only initializer.
module Registry =
    let private required input =
        TaskInputDeclaration.create TaskInputMode.Required input

    let private optional input =
        TaskInputDeclaration.create TaskInputMode.Optional input

    let private target input = TaskInputId.Target input
    let private field input = target (InputTarget.Field input)
    let private operation = required (target InputTarget.OperationId)
    let private caseTarget = required (field BusinessField.CaseReference)
    let private revision = required (target InputTarget.ExpectedRevision)

    let private bound input =
        { input with
            Source = InputSource.Binding
            Retention = InputRetention.Retained
        }

    let private declaration token aggregate target creates family consequence inputs collections =
        {
            Id = TaskId.ofLiteral token
            Exposure = TaskExposure.Client
            Aggregate = aggregate
            Target = target
            Creates = creates
            Family = family
            Consequence = consequence
            ReviewsFamilySequence = false
            Inputs = inputs
            Collections = collections
        }

    let private group mode group =
        BusinessField.ofGroup group
        |> List.map (fun input ->
            let selected =
                if
                    mode = TaskInputMode.Required && (BusinessField.describe input).AllowsAbsence
                then
                    TaskInputMode.Optional
                else
                    mode

            { TaskInputDeclaration.create selected (field input) with
                Clearable =
                    mode = TaskInputMode.Update && (BusinessField.describe input).AllowsAbsence
            })

    let private case kind =
        let description = CaseActKind.describe kind
        let creates = kind = CaseActKind.Open

        let values =
            match kind with
            | CaseActKind.Open -> group TaskInputMode.Required RecordGroup.Registration
            | CaseActKind.AmendRegistration -> group TaskInputMode.Update RecordGroup.Registration
            | CaseActKind.Decide -> group TaskInputMode.Required RecordGroup.Decision
            | CaseActKind.RecordPayment -> group TaskInputMode.Required RecordGroup.Payment
            | CaseActKind.RemovePayment -> [ required (target InputTarget.PaymentId) |> bound ]
            | CaseActKind.Correct ->
                [
                    required (target (InputTarget.Group RecordGroup.Registration))
                    required (target (InputTarget.Group RecordGroup.Decision))
                ]
                @ group TaskInputMode.Update RecordGroup.Registration
                @ group TaskInputMode.Update RecordGroup.Decision
            | CaseActKind.Void
            | CaseActKind.Reinstate -> [ required (target InputTarget.Reason) ]
            | CaseActKind.WithdrawDecision
            | CaseActKind.Close
            | CaseActKind.Reopen -> []
            | CaseActKind.Erase -> [ required (target InputTarget.ExpectedFamilySequence) ]

        let consequence =
            if description.Effect = CaseEffect.ErasePayload then
                Consequence.Irreversible
            elif description.Family = ActFamily.Stewardship then
                Consequence.Stewardship
            else
                Consequence.Routine

        { declaration
              (CaseActKind.token kind)
              AggregateKind.Family
              ActSubject.Case
              creates
              description.Family
              consequence
              (operation
               :: (if creates then caseTarget else bound caseTarget)
               :: revision
               :: values)
              [] with
            ReviewsFamilySequence = kind = CaseActKind.Erase
        }

    let private principal =
        [
            InputTarget.PrincipalKind
            InputTarget.PrincipalIssuer
            InputTarget.PrincipalSubject
        ]
        |> List.map (target >> required)

    let private profile =
        [
            required (target InputTarget.ProfileDisplayName)
            optional (target InputTarget.ProfileContact)
        ]

    let private grant =
        [
            required (target InputTarget.Role)
            required TaskInputId.Scope
            optional (field BusinessField.CaseReference)
            optional TaskInputId.CaseId
        ]
        |> List.map (fun input ->
            { input with
                Retention = InputRetention.Retained
            })

    let private people kind =
        let creates =
            match kind with
            | PeopleActKind.Register
            | PeopleActKind.Admit
            | PeopleActKind.ProvisionOwner -> true
            | _ -> false

        let values, collections =
            match kind with
            | PeopleActKind.Register
            | PeopleActKind.SetProfile -> profile, []
            | PeopleActKind.Admit
            | PeopleActKind.ProvisionOwner ->
                profile,
                [
                    {
                        Collection = TaskCollectionId.PeopleGrants
                        Inputs = grant
                    }
                ]
            | PeopleActKind.Grant
            | PeopleActKind.Revoke -> grant, []
            | PeopleActKind.Enable
            | PeopleActKind.Disable
            | PeopleActKind.EraseProfile -> [], []

        let identity =
            if creates then
                principal
            else
                [ required TaskInputId.ActorId |> bound; revision ]

        let consequence =
            match kind with
            | PeopleActKind.Disable -> Consequence.Stewardship
            | PeopleActKind.EraseProfile -> Consequence.Irreversible
            | _ -> Consequence.Routine

        { declaration
              (PeopleActKind.token kind)
              AggregateKind.Person
              ActSubject.People
              creates
              ActFamily.Business
              consequence
              (operation
               :: (identity @ values
                   |> List.map (fun input ->
                       if
                           kind = PeopleActKind.SetProfile
                           && input.Input = target InputTarget.ProfileContact
                       then
                           { input with Clearable = true }
                       else
                           input)))
              collections with
            Exposure =
                if kind = PeopleActKind.ProvisionOwner then
                    TaskExposure.Operator
                else
                    TaskExposure.Client
        }

    let private work =
        let values =
            [ field BusinessField.ResponsibleActor; field BusinessField.HandlingSummary ]
            |> List.map (fun input ->
                { optional input with
                    Clearable = true
                    Mode = TaskInputMode.Update
                })

        declaration
            "work.update"
            AggregateKind.Family
            ActSubject.Work
            false
            ActFamily.Business
            Consequence.Routine
            (operation
             :: bound caseTarget
             :: required (target InputTarget.ExpectedWorkRevision)
             :: values)
            []

    let private note kind =
        let values =
            match kind with
            | NoteActKind.AddNote -> [ required (target InputTarget.NoteText) ]
            | NoteActKind.RemoveNote -> [ required (target InputTarget.NoteId) |> bound ]

        declaration
            (NoteActKind.token kind)
            AggregateKind.Family
            ActSubject.Note
            (kind = NoteActKind.AddNote)
            ActFamily.Business
            Consequence.Routine
            (operation :: bound caseTarget :: values)
            []

    let private reminder kind =
        let identity =
            [
                required (target InputTarget.ReminderId) |> bound
                required (target InputTarget.ExpectedReminderRevision)
            ]

        let plan =
            [
                required (target InputTarget.ReminderAction)
                required (target InputTarget.ReminderDueDate)
            ]

        let values =
            match kind with
            | ReminderActKind.Create -> plan
            | ReminderActKind.Update
            | ReminderActKind.CompleteAndPlanNext -> identity @ plan
            | ReminderActKind.Reopen -> identity @ [ required (target InputTarget.ReminderDueDate) ]
            | ReminderActKind.Complete
            | ReminderActKind.Cancel -> identity

        declaration
            (ReminderActKind.token kind)
            AggregateKind.Family
            ActSubject.Reminder
            (kind = ReminderActKind.Create)
            ActFamily.Business
            Consequence.Routine
            (operation :: bound caseTarget :: values)
            []

    let declarations =
        [
            case CaseActKind.Open
            case CaseActKind.AmendRegistration
            case CaseActKind.Correct
            case CaseActKind.Decide
            case CaseActKind.WithdrawDecision
            case CaseActKind.RecordPayment
            case CaseActKind.RemovePayment
            case CaseActKind.Void
            case CaseActKind.Reinstate
            case CaseActKind.Close
            case CaseActKind.Reopen
            case CaseActKind.Erase
            people PeopleActKind.ProvisionOwner
            people PeopleActKind.Register
            people PeopleActKind.Grant
            people PeopleActKind.Revoke
            people PeopleActKind.Enable
            people PeopleActKind.Disable
            people PeopleActKind.SetProfile
            people PeopleActKind.EraseProfile
            people PeopleActKind.Admit
            work
            note NoteActKind.AddNote
            note NoteActKind.RemoveNote
            reminder ReminderActKind.Create
            reminder ReminderActKind.Update
            reminder ReminderActKind.Complete
            reminder ReminderActKind.Cancel
            reminder ReminderActKind.Reopen
            reminder ReminderActKind.CompleteAndPlanNext
        ]

    let clientDeclarations =
        declarations |> List.filter (fun task -> task.Exposure = TaskExposure.Client)

    let private byId =
        declarations |> List.map (fun task -> task.Id, task) |> Map.ofList

    let findDeclaration id = Map.tryFind id byId
