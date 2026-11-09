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
        PaymentCurrency: string option
    }

type NoteHandle = { NoteId: NoteId; CanRemove: bool }

module NoteHandle =
    let ofCore (item: NoteReadModel) =
        {
            NoteId = item.NoteId
            CanRemove = item.CanRemove
        }

type NotePageInteraction =
    {
        CaseId: CaseId
        NextCursor: string option
        Tasks: TaskId list
        Notes: NoteHandle list
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

module ReminderInteraction =
    let ofCore (value: ReminderReadModel) =
        let item = value.Reminder
        let update = List.contains ReminderActKind.Update value.Acts

        {
            ReminderInteraction.CaseId = Reminder.caseId item
            ReminderId = Reminder.id item
            Revision = Reminder.revision item
            Tasks = value.Acts |> List.map (ActKind.Reminder >> TaskId.Operation)
            ActionSeed =
                if update then
                    Some(ReminderAction.format (Reminder.action item))
                else
                    None
            DueDateSeed =
                if update || List.contains ReminderActKind.Reopen value.Acts then
                    Some(CalendarDate.format (Reminder.due item))
                else
                    None
        }

type PaymentHandle =
    {
        PaymentId: OperationId
        CanRemove: bool
    }

type CaseInteraction =
    {
        CaseId: CaseId
        Revision: Revision
        WorkRevision: Revision
        History: HistoryAccess
        Tasks: TaskId list
        EditableInputs: TaskInputId list
        Correction: CorrectionChoices option
        Seeds: CaseInputSeeds
        Notes: NoteHandle list
        Payments: PaymentHandle list
        NextActions: ReminderInteraction list
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

type PaymentPageInteraction =
    {
        CaseId: CaseId
        Revision: Revision
        NextCursor: string option
        Payments: PaymentHandle list
    }

/// Choice identities bind to core-authored Screen rows; captions are never reconstructed from handles.
type ActorPageInteraction =
    {
        NextCursor: string option
        ActorIds: ActorId list
    }

type CasePageInteraction =
    {
        NextCursor: string option
        Tasks: TaskId list
        CaseIds: CaseId list
    }

type ReminderPageInteraction =
    {
        NextCursor: string option
        Reminders: ReminderInteraction list
    }

/// Row destinations bind authorized history effects without exposing their business DTOs.
type AuthorityHistoryHandle = { Key: string; CaseId: CaseId option }

type AuthorityHistoryInteraction =
    {
        NextCursor: string option
        Entries: AuthorityHistoryHandle list
    }

/// Request hints are unverified editable seeds, never evidence of an admitted account/profile.
type AccessRequestHandle =
    {
        Key: string
        Principal: PrincipalKey
        ProfileSeed: ActorProfileInput
    }

type AccessRequestInteraction =
    {
        NextCursor: string option
        Requests: AccessRequestHandle list
    }

/// Identity and permitted editable input for the person currently being reviewed.
type PersonInteraction =
    {
        ActorId: ActorId
        Principal: PrincipalKey
        NextCursor: string option
        Tasks: TaskId list
        ProfileSeed: ActorProfileInput option
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
    | PaymentPage of PaymentPageInteraction
    | Assignees of ActorPageInteraction
    | Cases of CasePageInteraction
    | Reminders of ReminderPageInteraction
    | People of ActorPageInteraction
    | AccessRequests of AccessRequestInteraction
    | Person of PersonInteraction
    | AuthorityHistory of AuthorityHistoryInteraction
    | NotePage of NotePageInteraction

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
        | InteractionContext.PaymentPage page -> page.NextCursor
        | InteractionContext.Assignees page -> page.NextCursor
        | InteractionContext.Cases page -> page.NextCursor
        | InteractionContext.Reminders page -> page.NextCursor
        | InteractionContext.People page -> page.NextCursor
        | InteractionContext.AccessRequests page -> page.NextCursor
        | InteractionContext.Person page -> page.NextCursor
        | InteractionContext.AuthorityHistory page -> page.NextCursor
        | InteractionContext.NotePage page -> page.NextCursor
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
                if
                    disclosed
                    && List.exists (fun (item: NoteReadModel) -> item.CanRemove) value.Notes.Items
                then
                    yield TaskId.Operation(ActKind.Note NoteActKind.RemoveNote)
            ]

        let seeds =
            {
                PaymentCurrency =
                    if disclosed && List.contains CaseActKind.RecordPayment actions then
                        fields
                        |> Option.bind (fun fields -> fields.Finance.Decision)
                        |> Option.map (Decision.payable >> Money.currency)
                    else
                        None
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
                        History = value.Advice.History
                        Tasks = tasks
                        EditableInputs = editable
                        Correction = if disclosed then value.Advice.Correction else None
                        Seeds = seeds
                        Payments =
                            if disclosed then
                                value.LatestPayments
                                |> List.map (fun payment ->
                                    {
                                        PaymentId = PaymentEntry.id payment
                                        CanRemove = List.contains CaseActKind.RemovePayment actions
                                    })
                            else
                                []
                        NextActions =
                            if disclosed then
                                value.NextActions |> List.map ReminderInteraction.ofCore
                            else
                                []
                        Notes =
                            if disclosed then
                                value.Notes.Items |> List.map NoteHandle.ofCore
                            else
                                []
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
            (InteractionContext.Cases
                {
                    NextCursor = value.NextCursor
                    Tasks = value.Offers |> List.map offer
                    CaseIds = value.Items |> List.map _.CaseId
                })
            HumanDisclosure.Current

    let assignees (value: AssigneePage) =
        resource
            value.Basis
            (AssigneesScreen.view value)
            (InteractionContext.Assignees
                {
                    NextCursor = value.NextCursor
                    ActorIds = value.Items |> List.map _.ActorId
                })
            HumanDisclosure.Current

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

    let payments (value: CasePaymentPage) =
        resource
            value.Basis
            (PaymentsScreen.page value)
            (InteractionContext.PaymentPage
                {
                    CaseId = value.CaseId
                    Revision = value.Revision
                    NextCursor = value.NextCursor
                    Payments =
                        value.Items
                        |> List.map (fun item ->
                            {
                                PaymentId = PaymentEntry.id item.Entry
                                CanRemove = item.CanRemove
                            })
                })
            (if CaseState.lifecycle value.State = Lifecycle.Erased then
                 HumanDisclosure.Erased
             else
                 HumanDisclosure.Current)

    let notes (value: CaseNotesPage) =
        resource
            value.Basis
            (NotesScreen.page value)
            (InteractionContext.NotePage
                {
                    CaseId = value.CaseId
                    NextCursor = value.NextCursor
                    Tasks =
                        [
                            if value.Notes.CanAdd then
                                yield TaskId.Operation(ActKind.Note NoteActKind.AddNote)
                            if
                                List.exists
                                    (fun (item: NoteReadModel) -> item.CanRemove)
                                    value.Notes.Items
                            then
                                yield TaskId.Operation(ActKind.Note NoteActKind.RemoveNote)
                        ]
                    Notes = value.Notes.Items |> List.map NoteHandle.ofCore
                })
            HumanDisclosure.Current

    let reminder (value: ReminderRead) =
        let context = ReminderInteraction.ofCore value.Item

        resource
            value.Basis
            (ReminderScreen.view value)
            (InteractionContext.Reminder context)
            HumanDisclosure.Current

    let reminders (value: ReminderPage) =
        resource
            value.Basis
            (ReminderListScreen.view value)
            (InteractionContext.Reminders
                {
                    NextCursor = value.NextCursor
                    Reminders = value.Items |> List.map ReminderInteraction.ofCore
                })
            HumanDisclosure.Current

    let actors (value: ActorReviewPage) =
        resource
            value.Basis
            (PeopleScreen.view value)
            (InteractionContext.People
                {
                    NextCursor = value.NextCursor
                    ActorIds = value.Items |> List.map _.Actor.ActorId
                })
            HumanDisclosure.Current

    let grants (value: AuthorityGrantPage) =
        resource
            value.Basis
            (PersonScreen.view value)
            (InteractionContext.Person
                {
                    ActorId = value.Actor.Actor.ActorId
                    Principal = value.Actor.Principal
                    NextCursor = value.NextCursor
                    Tasks = value.Offers |> List.map (ActKind.Authority >> TaskId.Operation)
                    ProfileSeed = value.ProfileSeed
                })
            HumanDisclosure.Current

    let history (value: AuthorityHistoryPage) =
        resource
            value.Basis
            (AccessHistoryScreen.view value)
            (InteractionContext.AuthorityHistory
                {
                    NextCursor = value.NextCursor
                    Entries =
                        value.Entries
                        |> List.map (fun item ->
                            {
                                Key = EffectPosition.format item.Position
                                CaseId =
                                    match item.Facts with
                                    | AuthorityHistoryFacts.Granted {
                                                                        Scope = GrantScope.Case(id,
                                                                                                _)
                                                                    }
                                    | AuthorityHistoryFacts.Revoked {
                                                                        Scope = GrantScope.Case(id,
                                                                                                _)
                                                                    } -> Some id
                                    | _ -> None
                            })
                })
            HumanDisclosure.Current

    let requests (value: AccessRequestPage) =
        resource
            value.Basis
            (AccessRequestsScreen.view value)
            (InteractionContext.AccessRequests
                {
                    NextCursor = value.NextCursor
                    Requests =
                        value.Items
                        |> List.map (fun item ->
                            {
                                Key = ScreenEntries.principalKey item.Principal
                                Principal = item.Principal
                                ProfileSeed =
                                    {
                                        DisplayName =
                                            item.Claims.Name
                                            |> Option.orElse item.Claims.PreferredUsername
                                            |> Option.map PartyName.format
                                            |> Option.defaultValue ""
                                        Contact = item.Claims.Email |> Option.map PartyName.format
                                    }
                            })
                })
            HumanDisclosure.Current

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
