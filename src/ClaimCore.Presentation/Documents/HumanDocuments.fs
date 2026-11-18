namespace ClaimCore.Presentation

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
        if not (List.forall Affordance.valid actions) then
            invalidArg (nameof actions) "A document requires valid declared input controls."

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

        let actions =
            if not disclosed then
                []
            else
                let controls =
                    value.Advice.Offers
                    |> List.choose (function
                        | TaskOffer.Available(task, binding, availability) ->
                            let action =
                                TaskAffordances.offered
                                    task
                                    (Some binding)
                                    availability.Seeds
                                    availability.Editable
                                    availability.Choices

                            let action =
                                let input = TaskAffordances.field BusinessField.PaymentDate

                                if List.contains input action.Editable then
                                    { action with
                                        Seeds =
                                            Map.add
                                                input
                                                (InputValue.Text(
                                                    value.Work.Basis.BusinessDate.ToString(
                                                        CalendarDate.ExactFormat,
                                                        System.Globalization.CultureInfo
                                                            .InvariantCulture
                                                    )
                                                ))
                                                action.Seeds
                                    }
                                else
                                    action

                            Some
                                { action with
                                    Blocked =
                                        availability.Blocked
                                        |> Option.map TaskAffordances.availability
                                }
                        | TaskOffer.Declared _ -> None)

                let offered, blocked =
                    controls |> List.partition (fun control -> control.Blocked.IsNone)

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

        let declarationInputs (action: Affordance) =
            TaskDeclarations.find (TaskId.token action.Task)
            |> Option.map _.Inputs
            |> Option.defaultValue []

        let isRecommended (action: Affordance) =
            action.Blocked.IsNone && value.Advice.Recommended = Some action.Task

        let supplies field mode (action: Affordance) =
            action.Blocked.IsNone
            && (declarationInputs action
                |> List.exists (fun input ->
                    input.Input = TaskAffordances.field field
                    && input.Source = InputSource.Authored
                    && input.Mode = mode))

        let paymentControls =
            actions
            |> List.filter (supplies BusinessField.PaymentDate TaskInputMode.Required)

        let decisionControls =
            actions
            |> List.filter (fun action ->
                not (isRecommended action)
                && supplies BusinessField.PayableAmount TaskInputMode.Required action)

        let registrationControls =
            actions
            |> List.filter (fun action ->
                not (isRecommended action)
                && supplies BusinessField.ClaimantName TaskInputMode.Update action
                && not (
                    declarationInputs action
                    |> List.exists (fun input ->
                        match input.Input with
                        | TaskInputId.Target(InputTarget.Group _) -> true
                        | _ -> false)
                ))

        let recommended, actions = actions |> List.partition isRecommended

        let placed =
            registrationControls @ decisionControls |> List.map _.Task |> Set.ofList

        let actions =
            actions |> List.filter (fun action -> not (Set.contains action.Task placed))

        let screen = CaseScreen.view value

        let screen =
            let regions =
                screen.Regions
                |> List.map (fun region ->
                    match region.Role with
                    | RegionRole.Facts ->
                        { region with
                            Actions = registrationControls
                        }
                    | RegionRole.Payments ->
                        { region with
                            Actions = decisionControls
                        }
                    | _ -> region)

            let context =
                regions
                |> List.tryPick (fun region ->
                    match region.Role, region.Content with
                    | RegionRole.Payments, RegionContent.Table table when
                        not paymentControls.IsEmpty
                        ->
                        let entries =
                            table.Sections
                            |> List.collect _.Entries
                            |> List.filter (fun entry -> entry.Key = "remaining")

                        let context = ScreenEntries.entryRegion RegionRole.Context None entries

                        Some
                            { context with
                                Actions = paymentControls
                            }
                    | _ -> None)

            Screen.create (regions @ Option.toList context)
            |> Screen.withCurrentGroups screen.CurrentGroups

        let screen =
            if recommended.IsEmpty then
                screen
            else
                let region = ScreenEntries.entryRegion RegionRole.Recommendation None []

                Screen.create ({ region with Actions = recommended } :: screen.Regions)
                |> Screen.withCurrentGroups screen.CurrentGroups

        HumanDocument.create
            (DocumentSubject.Case(
                value.Snapshot.CaseId,
                value.Snapshot.Revision,
                value.Work.Revision
            ))
            screen
            actions
            (if disclosed then
                 [
                     {
                         NamedLink.Relation = LinkRelation.View
                         LabelId = "label.caseReminders"
                         Link =
                             Link.remindersForCase (
                                 CaseSelector.Reference(
                                     CaseReference.format value.Snapshot.Reference
                                 )
                             )
                     }
                 ]
             else
                 [])
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
            let task =
                ClaimCore.Domain.Tasks.Registry.clientDeclarations
                |> List.find (fun declaration ->
                    TaskDeclaration.bindingShape declaration = BindingShape.CaseNew)
                |> _.Id

            TaskAffordances.offered
                task
                (Some(Binding.CaseNew Revision.zero))
                Map.empty
                (TaskAffordances.authored task)
                Map.empty
        | InstallationOffer.PeopleAct kind ->
            let task = kind

            let seeds =
                [
                    TaskAffordances.input InputTarget.PrincipalKind, TaskAffordances.text "HUMAN"
                    match issuer with
                    | Some value ->
                        TaskAffordances.input InputTarget.PrincipalIssuer,
                        TaskAffordances.text value
                    | None -> ()
                ]
                |> Map.ofList

            TaskAffordances.offered
                task
                (Some(Binding.PeopleNew Revision.zero))
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
            (actions @ [ Affordance.command CommandRequest.TerminateOwnSessions ])
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
                     (WorkViewsScreen.labelId (CaseView.token item.View))
                     LinkRelation.View
                     (caseList (Some item.View))))
            @ (value.ReminderViews
               |> List.map (fun item ->
                   named
                       (WorkViewsScreen.labelId (ReminderView.token item.View))
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

        let actions =
            (value.Controls |> List.choose TaskAffordances.fromOffer)
            @ (if value.CanTerminateSessions then
                   [ Affordance.command (CommandRequest.TerminatePersonSessions actor) ]
               else
                   [])

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

    let terminated (value: SessionTermination) =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen =
                Screen.create
                    [
                        ScreenEntries.region
                            RegionRole.Notice
                            None
                            (RegionContent.Notice
                                {
                                    MessageId = "label.sessionsTerminated"
                                    Reason = None
                                    Tone = NoticeTone.Info
                                })
                        ScreenEntries.entryRegion
                            RegionRole.Details
                            None
                            [
                                ScreenEntries.value
                                    "count"
                                    "label.browserSessionsEnded"
                                    Prominence.Primary
                                    (PresentedValue.Count value.Count)
                            ]
                    ]
        }

    let terminationUnknown () =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen =
                Screen.create
                    [
                        ScreenEntries.region
                            RegionRole.Notice
                            None
                            (RegionContent.Notice
                                {
                                    MessageId = "label.sessionTerminationUnknown"
                                    Reason = None
                                    Tone = NoticeTone.Warning
                                })
                    ]
        }

module HumanReadReply =
    let classify (reply: HumanReadReply) =
        match reply.Outcome with
        | HumanReadOutcome.Succeeded _ -> ReplyKind.Succeeded
        | HumanReadOutcome.Rejected _ -> ReplyKind.Rejected
        | HumanReadOutcome.Failed _ -> ReplyKind.Failed

/// Operational uncertainty has no ledger identity or automatic retry meaning.
[<RequireQualifiedAccess>]
type HumanSessionTerminationResult =
    | Succeeded of self: bool * HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault
    | OutcomeUnknown of HumanDocument
