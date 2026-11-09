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

type WorkInputSeed =
    {
        Summary: string option
        Responsible: ActorId option
    }

type CaseInputSeeds =
    {
        Registration: RegistrationInput option
        Decision: DecisionInput option
        Work: WorkInputSeed option
    }

type CaseInteraction =
    {
        CaseId: CaseId
        Revision: Revision
        WorkRevision: Revision
        Tasks: TaskId list
        EditableInputs: TaskInputId list
        Correction: CorrectionChoices option
        Seeds: CaseInputSeeds
    }

type RequesterInteraction =
    {
        State: RequesterKind
        InstallationId: System.Guid option
        ActorId: ActorId option
        Views: View list
        Tasks: TaskId list
        HasScopedCases: bool
    }

type PageInteraction =
    {
        NextCursor: string option
        Tasks: TaskId list
    }

type ReminderInteraction =
    {
        CaseId: CaseId
        ReminderId: ReminderId
        Revision: Revision
        Tasks: TaskId list
        ActionSeed: string option
        DueDateSeed: string option
    }

/// Reviewed versions are control bindings, never copies of saved business state.
type ReminderIdentity =
    {
        ReminderId: ReminderId
        Revision: Revision
    }

[<RequireQualifiedAccess>]
type ReviewTarget =
    | Case of CaseId * before: Revision option
    | Work of CaseId * before: Revision
    | Note of CaseId * existing: NoteId option
    | Reminder of CaseId * existing: ReminderIdentity option
    | Authority of ActorId option * GrantScope

[<RequireQualifiedAccess>]
type ConfirmationTarget =
    | CaseReference of CaseReference
    | Operation of System.Guid

type ReviewInteraction =
    {
        Task: TaskId
        Target: ReviewTarget
        Inputs: TaskInputId list
        Consequence: Consequence
        Confirmation: ConfirmationTarget
    }

[<RequireQualifiedAccess>]
type ReceiptTarget =
    | Case of CaseId * Revision
    | Work of CaseId * Revision
    | Note of CaseId * NoteId
    | Reminder of CaseId * ReminderIdentity list
    | Authority of ActorId

type ReceiptInteraction =
    {
        Target: ReceiptTarget
        RefreshCase: CaseId option
    }

[<RequireQualifiedAccess>]
type InteractionContext =
    | Empty
    | Case of CaseInteraction
    | Continuation of nextCursor: string option
    | Requester of RequesterInteraction
    | Page of PageInteraction
    | Reminder of ReminderInteraction
    | Review of ReviewInteraction
    | Receipt of ReceiptInteraction

type HumanDocument =
    {
        Screen: Screen
        Context: InteractionContext
        Basis: ObservationBasis
        Disclosure: HumanDisclosure
    }

type HumanReview =
    {
        Document: HumanDocument
        Presentation: ReviewPresentation
    }

type HumanAdvice =
    {
        Message: PresentedMessage
        Action: RecommendedAction
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
    | Failed of HumanAdvice

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
    | Failed of HumanAdvice

[<RequireQualifiedAccess>]
type HumanExecuteResult =
    | Accepted of HumanDocument
    | AlreadyAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of HumanAdvice
    | OutcomeUnknown

[<RequireQualifiedAccess>]
type HumanLookupResult =
    | Accepted of HumanDocument * byRequester: bool
    | Pending of finalAt: System.DateTimeOffset * HumanDocument
    | NotAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of HumanAdvice

module HumanDocument =
    let nextCursor document =
        match document.Context with
        | InteractionContext.Continuation cursor -> cursor
        | InteractionContext.Page page -> page.NextCursor
        | _ -> None

    let empty disclosure =
        {
            Screen = Screen.create []
            Context = InteractionContext.Empty
            Basis = ObservationBasis.Unavailable
            Disclosure = disclosure
        }

module HumanCaseDocument =
    let private operation act = TaskId.Operation(ActKind.Case act)

    let ofCore (value: CaseReadModel) =
        let disclosed = CaseState.lifecycle value.Snapshot.State <> Lifecycle.Erased
        let canSummary = disclosed && value.Work.CanUpdateSummary
        let canAssign = disclosed && value.Work.CanAssign
        let fields = CaseState.fields value.Snapshot.State
        let actions = if disclosed then value.Advice.Acts else []

        let correct group =
            (if disclosed then value.Advice.Correction else None)
            |> Option.exists (fun choices ->
                (match group with
                 | RecordGroup.Registration -> choices.Registration
                 | RecordGroup.Decision -> choices.Decision
                 | RecordGroup.Payment -> [])
                |> List.contains CorrectionGroupAction.Update)

        let registration =
            List.contains CaseActKind.AmendRegistration actions
            || correct RecordGroup.Registration

        let decision =
            List.contains CaseActKind.Decide actions || correct RecordGroup.Decision

        let editable =
            [
                if registration then
                    yield! BusinessField.ofGroup RecordGroup.Registration
                if decision then
                    yield! BusinessField.ofGroup RecordGroup.Decision
                if canSummary then
                    yield BusinessField.HandlingSummary
                if canAssign then
                    yield BusinessField.ResponsibleActor
            ]
            |> List.map (InputTarget.Field >> TaskInputId.Target)

        let tasks =
            (actions |> List.map operation)
            @ [
                if canSummary || canAssign then
                    yield TaskId.Operation(ActKind.Work WorkActKind.UpdateWork)
                if disclosed && value.Work.CanCreateReminder then
                    yield TaskId.Operation(ActKind.Reminder ReminderActKind.Create)
                if disclosed && value.Notes.CanAdd then
                    yield TaskId.Operation(ActKind.Note NoteActKind.AddNote)
            ]

        let seeds =
            {
                Registration =
                    if registration then
                        fields
                        |> Option.map (fun fields -> Registration.authored fields.Registration)
                    else
                        None
                Decision =
                    if decision then
                        fields |> Option.bind _.Decision |> Option.map Decision.authored
                    else
                        None
                Work =
                    if canSummary || canAssign then
                        Some
                            {
                                Summary =
                                    if canSummary then
                                        value.Work.Summary |> Option.map HandlingSummary.format
                                    else
                                        None
                                Responsible =
                                    if canAssign then
                                        value.Work.Responsible |> Option.map _.ActorId
                                    else
                                        None
                            }
                    else
                        None
            }

        {
            Screen = CaseScreen.view value
            Context =
                InteractionContext.Case
                    {
                        CaseId = value.Snapshot.CaseId
                        Revision = value.Snapshot.Revision
                        WorkRevision = value.Work.Revision
                        Tasks = tasks
                        EditableInputs = editable
                        Correction = if disclosed then value.Advice.Correction else None
                        Seeds = seeds
                    }
            Basis = ObservationBasis.Snapshot value.Work.Basis
            Disclosure =
                if CaseState.lifecycle value.Snapshot.State = Lifecycle.Erased then
                    HumanDisclosure.Erased
                else
                    HumanDisclosure.Current
        }

module HumanReadDocuments =
    let private offer =
        function
        | InstallationOffer.Open -> TaskId.Operation(ActKind.Case CaseActKind.Open)
        | InstallationOffer.AuthorityAct kind -> TaskId.Operation(ActKind.Authority kind)

    let private resource basis screen context disclosure =
        {
            Screen = screen
            Context = context
            Basis = ObservationBasis.Snapshot basis
            Disclosure = disclosure
        }

    let private page basis cursor screen =
        resource
            basis
            screen
            (InteractionContext.Page { NextCursor = cursor; Tasks = [] })
            HumanDisclosure.Current

    let context (value: ContextReply) =
        let identity, basis =
            match value.Requester with
            | RequesterState.Active actor ->
                {
                    State = RequesterKind.Active
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.ActorId
                    Views = actor.View
                    Tasks = actor.Offers |> List.map offer
                    HasScopedCases = actor.HasScopedCases
                },
                ObservationBasis.Snapshot actor.Runtime.Basis
            | RequesterState.NoRoles actor ->
                {
                    State = RequesterKind.NoRoles
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.Self.ActorId
                    Views = []
                    Tasks = []
                    HasScopedCases = false
                },
                ObservationBasis.Unavailable
            | RequesterState.Disabled actor ->
                {
                    State = RequesterKind.Disabled
                    InstallationId = None
                    ActorId = Some actor.ActorId
                    Views = []
                    Tasks = []
                    HasScopedCases = false
                },
                ObservationBasis.Unavailable
            | RequesterState.NotRegistered _ ->
                {
                    State = RequesterKind.NotRegistered
                    InstallationId = None
                    ActorId = None
                    Views = []
                    Tasks = []
                    HasScopedCases = false
                },
                ObservationBasis.Unavailable

        {
            Screen = ContextScreen.view value
            Context = InteractionContext.Requester identity
            Basis = basis
            Disclosure = HumanDisclosure.Current
        }

    let cases (value: CaseSummaryPage) =
        resource
            value.Basis
            (CaseListScreen.view value)
            (InteractionContext.Page
                {
                    NextCursor = value.NextCursor
                    Tasks = value.Offers |> List.map offer
                })
            HumanDisclosure.Current

    let assignees (value: AssigneePage) =
        page value.Basis value.NextCursor (AssigneesScreen.view value)

    let summary (value: CaseWorklistSummary) =
        resource
            value.Basis
            (CaseTotalsScreen.view value)
            InteractionContext.Empty
            HumanDisclosure.Current

    let views (value: WorkViews) =
        resource
            value.Basis
            (WorkViewsScreen.view value)
            InteractionContext.Empty
            HumanDisclosure.Current

    let timeline (value: CaseTimelinePage) =
        { page value.Basis value.NextCursor (TimelineScreen.view value) with
            Disclosure =
                match value.Disclosure with
                | HistoryDisclosure.Full -> HumanDisclosure.Full
                | HistoryDisclosure.Summary -> HumanDisclosure.Summary
                | HistoryDisclosure.Erased -> HumanDisclosure.Erased
        }

    let notes (value: CaseNotesPage) =
        page value.Basis value.NextCursor (NotesScreen.page value)

    let reminder (value: ReminderRead) =
        let item = value.Item.Reminder
        let update = List.contains ReminderActKind.Update value.Item.Acts

        let context =
            {
                ReminderInteraction.CaseId = Reminder.caseId item
                ReminderId = Reminder.id item
                Revision = Reminder.revision item
                Tasks = value.Item.Acts |> List.map (ActKind.Reminder >> TaskId.Operation)
                ActionSeed =
                    if update then
                        Some(ReminderAction.format (Reminder.action item))
                    else
                        None
                DueDateSeed =
                    if update || List.contains ReminderActKind.Reopen value.Item.Acts then
                        Some(CalendarDate.format (Reminder.due item))
                    else
                        None
            }

        resource
            value.Basis
            (ReminderScreen.view value)
            (InteractionContext.Reminder context)
            HumanDisclosure.Current

    let reminders (value: ReminderPage) =
        page value.Basis value.NextCursor (ReminderListScreen.view value)

    let actors (value: ActorReviewPage) =
        page value.Basis value.NextCursor (PeopleScreen.view value)

    let grants (value: AuthorityGrantPage) =
        page value.Basis value.NextCursor (PersonScreen.view value)

    let history (value: AuthorityHistoryPage) =
        page value.Basis value.NextCursor (AccessHistoryScreen.view value)

    let requests (value: AccessRequestPage) =
        page value.Basis value.NextCursor (AccessRequestsScreen.view value)

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
