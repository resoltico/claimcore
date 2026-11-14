namespace ClaimCore.Presentation

open ClaimCore.CommandLine
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type HumanDisclosure =
    | Current
    | Partial
    | Full
    | Summary
    | Withheld
    | Erased
    | None

[<RequireQualifiedAccess>]
type ObservationBasis =
    | Snapshot of WorkDateBasis
    | Unavailable

[<RequireQualifiedAccess>]
type ConfirmationTarget =
    | CaseReference of CaseReference
    | Operation of System.Guid

type RequesterSubject =
    {
        State: RequesterKind
        InstallationId: System.Guid option
        ActorId: ActorId option
    }

[<RequireQualifiedAccess>]
type DocumentSubject =
    | Case of CaseId * revision: Revision * workRevision: Revision
    | Person of ActorId
    | Requester of RequesterSubject
    | List
    | Review of ConfirmationTarget * Consequence
    | Receipt of OperationId
    | Operation of System.Guid

type HumanDocument =
    {
        Subject: DocumentSubject
        Screen: Screen
        Actions: Affordance list
        Links: NamedLink list
        NextCursor: string option
        Basis: ObservationBasis
        Disclosure: HumanDisclosure
        History: HistoryAccess option
    }

type HumanReview =
    {
        Document: HumanDocument
        Presentation: ReviewPresentation
    }

type HumanInputIssue =
    {
        Input: TaskInputId
        Row: TaskRowTarget option
        Message: PresentedMessage
        Related: Entry option
    }

[<RequireQualifiedAccess>]
type HumanRejection =
    | Input of HumanAdvice * HumanInputIssue list
    | Conflict of HumanAdvice * actualRevision: Revision * HumanDocument
    | Refused of HumanAdvice

[<RequireQualifiedAccess>]
type HumanReadOutcome =
    | Succeeded of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

type HumanReadReply = { Outcome: HumanReadOutcome }

/// The transport envelope owns correlation; each success has a single HUMAN document.
type HumanOperationReply<'outcome> =
    {
        OperationId: System.Guid
        Outcome: 'outcome
    }

[<RequireQualifiedAccess>]
type HumanPreviewResult =
    | Reviewed of HumanReview
    | AlreadyAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

[<RequireQualifiedAccess>]
type HumanExecuteResult =
    | Accepted of HumanDocument
    | AlreadyAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault
    | OutcomeUnknown

[<RequireQualifiedAccess>]
type HumanLookupResult =
    | Accepted of HumanDocument * byRequester: bool
    | Pending of finalAt: System.DateTimeOffset * HumanDocument
    | NotAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

module HumanDocument =
    let create
        subject
        screen
        actions
        links
        cursor
        basis
        disclosure
        (history: HistoryAccess option)
        =
        if
            (actions |> List.filter _.Primary |> List.length) > 1
            || not (List.forall Affordance.valid actions)
        then
            invalidArg
                (nameof actions)
                "A document requires valid affordances and at most one primary action."

        let screen =
            Screen.create screen.Regions |> Screen.withCurrentGroups screen.CurrentGroups

        match subject with
        | DocumentSubject.Requester identity when identity.State = RequesterKind.Active ->
            if
                identity.InstallationId.IsNone
                || identity.ActorId.IsNone
                || basis = ObservationBasis.Unavailable
            then
                invalidArg
                    (nameof subject)
                    "An active requester requires its authenticated identity and observation basis."
        | _ -> ()

        let controls = actions @ Screen.affordances screen

        if
            List.contains
                disclosure
                [ HumanDisclosure.Withheld; HumanDisclosure.Erased; HumanDisclosure.None ]
            && not controls.IsEmpty
        then
            invalidArg (nameof disclosure) "Undisclosed resources cannot retain authoring controls."

        if
            history.IsSome
            && (match subject with
                | DocumentSubject.Case _ -> false
                | _ -> true)
        then
            invalidArg (nameof history) "History access belongs only to a case document."

        {
            Subject = subject
            Screen = screen
            Actions = actions
            Links = links
            NextCursor = cursor
            Basis = basis
            Disclosure = disclosure
            History = history
        }

    let empty disclosure =
        create
            DocumentSubject.List
            (Screen.create [])
            []
            []
            None
            ObservationBasis.Unavailable
            disclosure
            None

module HumanCaseDocument =
    let ofCore (value: CaseReadModel) =
        let disclosed = CaseState.lifecycle value.Snapshot.State <> Lifecycle.Erased
        let current = CaseState.fields value.Snapshot.State
        let taskInputs task = TaskAffordances.authored task

        let canCorrect group =
            value.Advice.Correction
            |> Option.exists (fun choices ->
                let choices =
                    if group = RecordGroup.Registration then
                        choices.Registration
                    else
                        choices.Decision

                List.contains CorrectionGroupAction.Update choices)

        let actions =
            if not disclosed then
                []
            else
                let offered =
                    value.Advice.Acts
                    |> List.filter ((<>) CaseActKind.RemovePayment)
                    |> List.map (fun kind ->
                        let task = TaskId.Operation(ActKind.Case kind)

                        let registration =
                            if
                                kind = CaseActKind.AmendRegistration
                                || (kind = CaseActKind.Correct
                                    && canCorrect RecordGroup.Registration)
                            then
                                current
                                |> Option.map (fun fields ->
                                    Registration.authored fields.Registration
                                    |> RegistrationInput.values
                                    |> TaskAffordances.fields)
                                |> Option.defaultValue Map.empty
                            else
                                Map.empty

                        let decision =
                            if
                                kind = CaseActKind.Decide
                                || (kind = CaseActKind.Correct && canCorrect RecordGroup.Decision)
                            then
                                current
                                |> Option.bind _.Decision
                                |> Option.map (
                                    Decision.authored
                                    >> DecisionInput.values
                                    >> TaskAffordances.fields
                                )
                                |> Option.defaultValue Map.empty
                            else
                                Map.empty

                        let seeds =
                            Map.fold
                                (fun seed input value -> Map.add input value seed)
                                registration
                                decision

                        let seeds =
                            if kind = CaseActKind.Correct then
                                seeds
                                |> Map.add
                                    (TaskAffordances.input (
                                        InputTarget.Group RecordGroup.Registration
                                    ))
                                    (TaskAffordances.text "KEEP")
                                |> Map.add
                                    (TaskAffordances.input (InputTarget.Group RecordGroup.Decision))
                                    (TaskAffordances.text "KEEP")
                            else
                                seeds

                        let seeds =
                            if kind = CaseActKind.RecordPayment then
                                current
                                |> Option.bind (fun fields -> fields.Finance.Decision)
                                |> Option.map (fun decision ->
                                    seeds
                                    |> Map.add
                                        (TaskAffordances.field BusinessField.PaidCurrency)
                                        (TaskAffordances.text (
                                            Money.currency (Decision.payable decision)
                                        )))
                                |> Option.defaultValue seeds
                            else
                                seeds

                        let choices =
                            if kind = CaseActKind.Correct then
                                value.Advice.Correction
                                |> Option.map (fun choices ->
                                    Map.ofList
                                        [
                                            TaskAffordances.input (
                                                InputTarget.Group RecordGroup.Registration
                                            ),
                                            choices.Registration
                                            |> List.map CorrectionGroupAction.token
                                            TaskAffordances.input (
                                                InputTarget.Group RecordGroup.Decision
                                            ),
                                            choices.Decision
                                            |> List.map CorrectionGroupAction.token
                                        ])
                                |> Option.defaultValue Map.empty
                            else
                                Map.empty

                        let editable =
                            taskInputs task
                            |> List.filter (fun input ->
                                match kind, input, value.Advice.Correction with
                                | CaseActKind.Correct,
                                  TaskInputId.Target(InputTarget.Field field),
                                  Some choices ->
                                    match (BusinessField.describe field).Group with
                                    | Some RecordGroup.Registration ->
                                        List.contains
                                            CorrectionGroupAction.Update
                                            choices.Registration
                                    | Some RecordGroup.Decision ->
                                        List.contains
                                            CorrectionGroupAction.Update
                                            choices.Decision
                                    | _ -> true
                                | _ -> true)

                        let action =
                            TaskAffordances.caseAction
                                kind
                                value.Snapshot.CaseId
                                value.Snapshot.Revision
                                seeds
                                editable
                                choices

                        { action with
                            Primary = value.Advice.Recommended = Some kind
                        })

                let blocked =
                    value.Advice.Blocked
                    |> List.filter (fun value -> value.Act <> CaseActKind.RemovePayment)
                    |> List.map (fun blocked ->
                        let action =
                            TaskAffordances.caseAction
                                blocked.Act
                                value.Snapshot.CaseId
                                value.Snapshot.Revision
                                Map.empty
                                []
                                Map.empty

                        { action with
                            Blocked = Some(TaskAffordances.availability blocked.Reason)
                        })

                let work =
                    if value.Work.CanUpdateSummary || value.Work.CanAssign then
                        let seeds =
                            [
                                if value.Work.CanUpdateSummary then
                                    match value.Work.Summary with
                                    | Some value ->
                                        TaskAffordances.field BusinessField.HandlingSummary,
                                        TaskAffordances.text (HandlingSummary.format value)
                                    | None -> ()
                                if value.Work.CanAssign then
                                    match value.Work.Responsible with
                                    | Some actor ->
                                        TaskAffordances.field BusinessField.ResponsibleActor,
                                        TaskAffordances.text (ActorId.format actor.ActorId)
                                    | None -> ()
                            ]
                            |> Map.ofList

                        let editable =
                            [
                                if value.Work.CanUpdateSummary then
                                    TaskAffordances.field BusinessField.HandlingSummary
                                if value.Work.CanAssign then
                                    TaskAffordances.field BusinessField.ResponsibleActor
                            ]

                        [
                            TaskAffordances.work
                                value.Snapshot.CaseId
                                value.Work.Revision
                                seeds
                                editable
                        ]
                    else
                        []

                offered
                @ blocked
                @ work
                @ [
                    if value.Work.CanCreateReminder then
                        yield TaskAffordances.reminderCreate value.Snapshot.CaseId
                    if value.Notes.CanAdd then
                        yield TaskAffordances.noteAdd value.Snapshot.CaseId
                ]

        HumanDocument.create
            (DocumentSubject.Case(
                value.Snapshot.CaseId,
                value.Snapshot.Revision,
                value.Work.Revision
            ))
            (CaseScreen.view value)
            actions
            []
            None
            (ObservationBasis.Snapshot value.Work.Basis)
            (if disclosed then
                 HumanDisclosure.Current
             else
                 HumanDisclosure.Erased)
            (Some value.Advice.History)

module HumanReadDocuments =
    let private resource basis screen subject actions links cursor disclosure history =
        HumanDocument.create
            subject
            screen
            actions
            links
            cursor
            (ObservationBasis.Snapshot basis)
            disclosure
            history

    let private listPage basis screen cursor =
        resource basis screen DocumentSubject.List [] [] cursor HumanDisclosure.Current None

    let private caseList view =
        Link.CaseList
            {
                Page =
                    {
                        AfterCursor = None
                        Limit = Paging.DefaultPageSize
                    }
                Filter =
                    { CaseListFilter.empty<string> with
                        View = view
                    }
            }

    let private reminderList view =
        Link.ReminderList
            {
                View = Some view
                Target = None
                State = None
                Due = None
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = Paging.DefaultPageSize
                IncludeInactive = None
            }

    let private named label relation link =
        {
            NamedLink.Relation = relation
            LabelId = label
            Link = link
        }

    let private installationOffer issuer =
        function
        | InstallationOffer.Open ->
            let task = TaskId.Operation(ActKind.Case CaseActKind.Open)

            TaskAffordances.offered
                task
                (OperationBinding.Case(CaseSelector.Reference "", 0L, None))
                Map.empty
                (TaskAffordances.input (InputTarget.Field BusinessField.CaseReference)
                 :: TaskAffordances.authored task)
                Map.empty
        | InstallationOffer.PeopleAct kind ->
            let task = TaskId.Operation(ActKind.People kind)

            let seeds =
                [
                    TaskAffordances.input InputTarget.PrincipalKind, TaskAffordances.text "HUMAN"
                    if kind = PeopleActKind.Admit then
                        TaskAffordances.input InputTarget.Grants, TaskAffordances.initialGrantRows
                    match issuer with
                    | Some value ->
                        TaskAffordances.input InputTarget.PrincipalIssuer,
                        TaskAffordances.text value
                    | None -> ()
                ]
                |> Map.ofList

            TaskAffordances.offered
                task
                (OperationBinding.People None)
                seeds
                (TaskAffordances.authored task)
                Map.empty

    let context (value: ContextReply) =
        let identity, basis, actions, links =
            match value.Requester with
            | RequesterState.Active actor ->
                let actions =
                    if actor.Maintenance.Active then
                        []
                    else
                        actor.Offers |> List.map (installationOffer (Some actor.Issuer))

                let links =
                    actor.View
                    |> List.map (function
                        | View.Cases -> named "view.CASES.label" LinkRelation.View (caseList None)
                        | View.People ->
                            named
                                "view.PEOPLE.label"
                                LinkRelation.View
                                (Link.PeopleList
                                    {
                                        Role = None
                                        AfterCursor = None
                                        Limit = Paging.DefaultPageSize
                                        Text = None
                                    })
                        | View.Operations ->
                            named "view.OPERATIONS.label" LinkRelation.View Link.OperationLookupForm)

                let links =
                    links
                    @ [
                        if List.contains View.Cases actor.View then
                            yield
                                named
                                    "label.reminders"
                                    LinkRelation.View
                                    (reminderList ReminderView.Pending)
                        if List.contains Capability.ReadAccess actor.Capability then
                            yield
                                named
                                    "endpoint.people.history.label"
                                    LinkRelation.View
                                    (Link.PeopleHistory
                                        {
                                            TargetActorId = None
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        })
                        if List.contains Capability.ManageAccess actor.Capability then
                            yield
                                named
                                    "endpoint.people.requests.label"
                                    LinkRelation.View
                                    (Link.PeopleRequests
                                        {
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        })
                    ]

                {
                    State = RequesterKind.Active
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.ActorId
                },
                ObservationBasis.Snapshot actor.Runtime.Basis,
                actions,
                links
            | RequesterState.NoRoles actor ->
                {
                    State = RequesterKind.NoRoles
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.Self.ActorId
                },
                ObservationBasis.Unavailable,
                [],
                []
            | RequesterState.Disabled actor ->
                {
                    State = RequesterKind.Disabled
                    InstallationId = None
                    ActorId = Some actor.ActorId
                },
                ObservationBasis.Unavailable,
                [],
                []
            | RequesterState.NotRegistered _ ->
                {
                    State = RequesterKind.NotRegistered
                    InstallationId = None
                    ActorId = None
                },
                ObservationBasis.Unavailable,
                [],
                []

        HumanDocument.create
            (DocumentSubject.Requester identity)
            (ContextScreen.view value)
            actions
            links
            None
            basis
            HumanDisclosure.Current
            None

    let cases (value: CaseSummaryPage) =
        let links =
            value.ExactReference
            |> Option.map (fun value ->
                named
                    "field.caseReference.label"
                    LinkRelation.OpenCase
                    (Link.caseById value.CaseId))
            |> Option.toList

        resource
            value.Basis
            (CaseListScreen.view value)
            DocumentSubject.List
            (value.Offers |> List.map (installationOffer None))
            links
            value.NextCursor
            HumanDisclosure.Current
            None

    let assignees (value: AssigneePage) =
        listPage value.Basis (AssigneesScreen.view value) value.NextCursor

    let summary (value: CaseWorklistSummary) =
        listPage value.Basis (CaseTotalsScreen.view value) None

    let views (value: WorkViews) =
        let links =
            (value.CaseViews
             |> List.map (fun item ->
                 named
                     (Vocabulary.messageKey CaseView.vocabulary item.View "label")
                     LinkRelation.View
                     (caseList (Some item.View))))
            @ (value.ReminderViews
               |> List.map (fun item ->
                   named
                       (Vocabulary.messageKey ReminderView.vocabulary item.View "label")
                       LinkRelation.View
                       (reminderList item.View)))

        resource
            value.Basis
            (WorkViewsScreen.view value)
            DocumentSubject.List
            []
            links
            None
            HumanDisclosure.Current
            None

    let timeline (value: CaseTimelinePage) =
        let disclosure =
            match value.Disclosure with
            | HistoryDisclosure.Full -> HumanDisclosure.Full
            | HistoryDisclosure.Summary -> HumanDisclosure.Summary
            | HistoryDisclosure.Erased -> HumanDisclosure.Erased

        resource
            value.Basis
            (TimelineScreen.view value)
            DocumentSubject.List
            []
            []
            value.NextCursor
            disclosure
            None

    let payments (value: CasePaymentPage) =
        resource
            value.Basis
            (PaymentsScreen.page value)
            (DocumentSubject.Case(value.CaseId, value.Revision, value.WorkRevision))
            []
            []
            value.NextCursor
            (if CaseState.lifecycle value.State = Lifecycle.Erased then
                 HumanDisclosure.Erased
             else
                 HumanDisclosure.Current)
            None

    let notes (value: CaseNotesPage) =
        resource
            value.Basis
            (NotesScreen.page value)
            DocumentSubject.List
            (if value.Notes.CanAdd then
                 [ TaskAffordances.noteAdd value.CaseId ]
             else
                 [])
            [
                named "field.caseReference.label" LinkRelation.OpenCase (Link.caseById value.CaseId)
            ]
            value.NextCursor
            HumanDisclosure.Current
            None

    let reminder (value: ReminderRead) =
        resource
            value.Basis
            (ReminderScreen.view value)
            DocumentSubject.List
            (TaskAffordances.reminder value.Item)
            []
            None
            HumanDisclosure.Current
            None

    let reminders (value: ReminderPage) =
        listPage value.Basis (ReminderListScreen.view value) value.NextCursor

    let actors (value: ActorReviewPage) =
        listPage value.Basis (PeopleScreen.view value) value.NextCursor

    let grants (value: PeopleGrantPage) =
        let actor = value.Actor.Actor.ActorId

        let primary =
            [
                PeopleActKind.Grant
                PeopleActKind.Enable
                PeopleActKind.Revoke
                PeopleActKind.Disable
            ]
            |> List.tryFind (fun kind -> List.contains kind value.Offers)

        let actions =
            value.Offers
            |> List.map (fun kind ->
                let task = TaskId.Operation(ActKind.People kind)

                let seeds =
                    if kind = PeopleActKind.SetProfile then
                        value.ProfileSeed
                        |> Option.map TaskAffordances.profile
                        |> Option.defaultValue Map.empty
                    else
                        Map.empty

                let seeds =
                    if kind = PeopleActKind.Grant || kind = PeopleActKind.Revoke then
                        seeds |> Map.add TaskInputId.Scope (TaskAffordances.text "INSTALLATION")
                    else
                        seeds

                let choices =
                    if kind = PeopleActKind.Grant then
                        Map.ofList
                            [
                                TaskAffordances.input InputTarget.Role,
                                Role.all
                                |> List.filter (fun role ->
                                    PrincipalKey.isHuman value.Actor.Principal
                                    || not (Access.humanOnlyRole role))
                                |> List.map Role.token
                            ]
                    else
                        Map.empty

                let action =
                    TaskAffordances.person kind actor seeds (TaskAffordances.authored task) choices

                { action with
                    Primary = primary = Some kind
                })

        let links =
            value.Actor.ResponsibleCases
            |> Option.map (fun selection ->
                named
                    "field.responsibleActor.label"
                    LinkRelation.View
                    (Link.CaseList
                        {
                            Page =
                                {
                                    AfterCursor = None
                                    Limit = Paging.DefaultPageSize
                                }
                            Filter =
                                { CaseListFilter.activeOpen<string> with
                                    View = Some selection.View
                                    ResponsibleActor = Some selection.ResponsibleActor
                                }
                        }))
            |> Option.toList

        resource
            value.Basis
            (PersonScreen.view value)
            (DocumentSubject.Person actor)
            actions
            links
            value.NextCursor
            HumanDisclosure.Current
            None

    let history (value: PeopleHistoryPage) =
        listPage value.Basis (AccessHistoryScreen.view value) value.NextCursor

    let requests (value: AccessRequestPage) =
        listPage value.Basis (AccessRequestsScreen.view value) value.NextCursor

    let dismissed () =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen = AccessRequestsScreen.dismissed ()
        }

module HumanReadReply =
    let classify (reply: HumanReadReply) =
        match reply.Outcome with
        | HumanReadOutcome.Succeeded _ ->
            {
                Kind = ReplyKind.Succeeded
                ExitCode = ExitCode.Success
            }
        | HumanReadOutcome.Rejected _ ->
            {
                Kind = ReplyKind.Rejected
                ExitCode = ExitCode.Rejected
            }
        | HumanReadOutcome.Failed _ ->
            {
                Kind = ReplyKind.Failed
                ExitCode = ExitCode.Failed
            }
