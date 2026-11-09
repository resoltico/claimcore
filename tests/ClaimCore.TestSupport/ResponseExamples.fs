module ClaimCore.TestSupport.ResponseExamples

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

let private id = OperationIds.numberedAt Inputs.workBasis.ObservedAt 1
let private actorId = CaseFixtures.actorId id
let private actor = Inputs.namedActorReference actorId "Synthetic recorder"
let private reference = Inputs.reference "CASE-1"
let private basis = Inputs.workBasis

let private position =
    LedgerPosition.create Sequence.first (EntryHash.ofBytes (Array.zeroCreate 32) |> Option.get)

let snapshot: CaseSnapshot =
    {
        CaseId = CaseFixtures.caseId id
        Reference = reference
        Revision = Revision.first
        State =
            CaseState.Active
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration = Inputs.admittedRegistration Inputs.registration
                    Progress = PaymentProgress.Undecided
                }
    }

let receipt: OperationReceipt =
    {
        OperationId = CaseFixtures.operationId id
        Position = position
        RecordedAt = basis.ObservedAt
        RecordedBy = actor
        Basis = basis
        Subject =
            ReceiptSubject.Case
                {
                    CaseId = snapshot.CaseId
                    Reference = reference
                    Revision = Revision.first
                    Act = CaseActKind.Open
                    Snapshot =
                        SnapshotDisclosure.Disclosed(LiveCase.ofState snapshot.State |> Option.get)
                }
    }

let presentReceipt = Presented.create ReceiptScreen.view

let authorityReceipt =
    { receipt with
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActor = AuthorityTargetDisclosure.Disclosed actor
                    Act = AuthorityKind.ProvisionOwner
                    Effects =
                        [
                            {
                                Act = AuthorityKind.ProvisionOwner
                                Position = position
                            }
                        ]
                }
    }

let caseRead: CaseReadModel =
    {
        Snapshot = snapshot
        Advice = Inputs.caseAdvice snapshot.State
        Mine = false
        Work = Inputs.workView snapshot.State
        Headline = CaseHeadline.Undecided
        Attention = []
        Stages = Inputs.stageDisplay snapshot.State
        Disposition = None
        NextActions = []
        Notes =
            {
                Items = []
                Count = 0L
                CanAdd = true
            }
        ChangedAt = basis.ObservedAt
        RecordedBy = actor
    }

let private runtime =
    {
        RuntimeContext.ProductVersion = "synthetic"
        Basis = basis
    }

let private principal =
    PrincipalKey.human "https://issuer.example/" "synthetic"
    |> Result.defaultWith (string >> invalidOp)

let private directoryActor: ActorReviewItem =
    {
        Actor = actor
        Principal = principal
        Contact = None
        State = AccountState.NoRoles
        Roles = []
        ResponsibleCases = None
    }

let preview: PresentedPreviewReply =
    {
        OperationId = id
        Outcome =
            Outcome.Succeeded(
                PresentedPreviewed.Reviewed
                    {
                        View = Screen.create []
                        Subject =
                            PresentedReviewSubject.Case
                                {
                                    Before = Some snapshot
                                    Proposed =
                                        { snapshot with
                                            Revision = CaseFixtures.revision 2L
                                        }
                                    Context = runtime
                                }
                        Presentation =
                            {
                                Changes =
                                    [
                                        {
                                            Key = "claimantName"
                                            LabelId = "field.claimantName.label"
                                            Before = PresentedValue.Party(Inputs.name "Before")
                                            After = PresentedValue.Party(Inputs.name "After")
                                        }
                                    ]
                                Unchanged = []
                            }
                    }
            )
    }

let private reminder =
    Reminder.ofAccepted
        (ReminderId.ofOperationId receipt.OperationId)
        snapshot.CaseId
        Revision.first
        (ReminderAction.parse "Contact claimant"
         |> Result.defaultWith (string >> invalidOp))
        (CalendarDate.ofDate basis.BusinessDate)
        ReminderState.Pending
    |> Result.defaultWith invalidOp

let private read (endpoint: Endpoint<'request, Outcome<Presented<'reply>>>) describe success =
    [
        EndpointValues.output endpoint (Presented.outcome describe success)
        EndpointValues.output
            endpoint
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
        EndpointValues.output endpoint (Outcome.Failed CoreFault.StoreUnavailable)
    ]

/// Typed behavioral inputs are independent of the literal decoding examples used by conformance tests.
let private replies () : IEndpointOutput list =
    [
        yield!
            read
                Api.session
                (fun (reply: Session) ->
                    ContextScreen.sessionView reply.Authenticated reply.ContractIdentity)
                (Outcome.Succeeded
                    {
                        Session.Authenticated = true
                        ContractIdentity = ContractIdentity.value
                        AntiforgeryToken = Some "csrf"
                    })
        yield! read Api.logout (fun () -> ContextScreen.logoutView ()) (Outcome.Succeeded())
        yield!
            read
                Api.context
                ContextScreen.view
                (Outcome.Succeeded
                    {
                        Requester =
                            RequesterState.Active
                                {
                                    InstallationId =
                                        Guid.Parse "90000000-0000-4000-8000-000000000001"
                                    ActorId = actorId
                                    Self = actor
                                    Issuer = "https://issuer.example/"
                                    Roles = []
                                    HasScopedCases = false
                                    Maintenance = RegisterMaintenance.initial
                                    Runtime = runtime
                                    Capability =
                                        [
                                            Capability.ReadCase
                                            Capability.Register
                                            Capability.ManageAuthority
                                            Capability.ReadAuthority
                                        ]
                                    View =
                                        [
                                            ClaimCore.Application.Contracts.View.Cases
                                            ClaimCore.Application.Contracts.View.Authority
                                            ClaimCore.Application.Contracts.View.Operations
                                        ]
                                    Offers = [ InstallationOffer.Open ]
                                }
                    })
        yield! read Api.caseGet CaseScreen.view (Outcome.Succeeded caseRead)
        yield!
            read
                Api.caseNotes
                NotesScreen.page
                (Outcome.Succeeded
                    {
                        Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = snapshot.Reference
                        Notes =
                            {
                                Items = []
                                Count = 0L
                                CanAdd = false
                            }
                        NextCursor = None
                    })
        yield!
            read
                Api.caseList
                (fun (reply: CaseSummaryPage) -> CaseListScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        Offers = []
                        NextCursor = None
                        ExactReference = None
                    })
        yield!
            read
                Api.caseSummary
                CaseTotalsScreen.view
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Claimed = []
                        Payable = []
                        Paid = []
                        Counts =
                            {
                                Total = 0L
                                Assignment = []
                                Due = []
                                Lifecycles = []
                                Statuses = []
                                Progress = []
                            }
                    })
        yield!
            read
                Api.caseAssignees
                (fun (reply: AssigneePage) -> AssigneesScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.caseTimeline
                (fun (reply: CaseTimelinePage) -> TimelineScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Disclosure = HistoryDisclosure.Full
                        Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = reference
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.workViews
                WorkViewsScreen.view
                (Outcome.Succeeded
                    {
                        Basis = basis
                        CaseViews =
                            CaseView.all
                            |> List.map (fun view ->
                                {
                                    CaseViewCount.View = view
                                    Count = 0L
                                })
                        ReminderViews =
                            ReminderView.all
                            |> List.map (fun view ->
                                {
                                    ReminderViewCount.View = view
                                    Count = 0L
                                })
                    })
        yield!
            read
                Api.reminder
                ReminderScreen.view
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Item =
                            {
                                Reminder = reminder
                                Reference = reference
                                ClaimantName =
                                    CaseState.fields snapshot.State
                                    |> Option.map (fun fields ->
                                        Registration.claimant fields.Registration)
                                    |> Option.get
                                Responsible = None
                                RecordedAt = basis.ObservedAt
                                RecordedBy = actor
                                Acts = []
                                Active = true
                                Timing = Some ReminderDue.Today
                            }
                    })
        yield!
            read
                Api.reminders
                (fun (reply: ReminderPage) -> ReminderListScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityList
                (fun (reply: ActorReviewPage) -> PeopleScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityGrants
                (fun (reply: AuthorityGrantPage) -> PersonScreen.view reply)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Actor = directoryActor
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityHistory
                (fun (reply: AuthorityHistoryPage) -> AccessHistoryScreen.rows reply.Entries)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Entries = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityRequests
                (fun (reply: AccessRequestPage) -> AccessRequestsScreen.rows reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield! read Api.authorityRequestsDismiss (fun () -> Screen.create []) (Outcome.Succeeded())
        for outcome in
            [
                preview.Outcome
                Outcome.Succeeded(
                    PresentedPreviewed.AlreadyAccepted(Presented.create ReceiptScreen.view receipt)
                )
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            yield EndpointValues.output Api.operationPreview { preview with Outcome = outcome }
        for outcome in
            [
                PresentedExecuteResult.Accepted(Presented.create ReceiptScreen.view receipt)
                PresentedExecuteResult.AlreadyAccepted(Presented.create ReceiptScreen.view receipt)
                PresentedExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                PresentedExecuteResult.Failed CoreFault.StoreUnavailable
                PresentedExecuteResult.OutcomeUnknown
            ] do
            yield
                EndpointValues.output
                    Api.operationExecute
                    {
                        PresentedExecuteReply.OperationId = id
                        Outcome = outcome
                    }
        for outcome in
            [
                Outcome.Succeeded(
                    PresentedLookupResult.Accepted(
                        Presented.create ReceiptScreen.view receipt,
                        true
                    )
                )
                Outcome.Succeeded(
                    PresentedLookupResult.Pending(
                        OperationLifetime.finalAt receipt.OperationId,
                        OperationScreen.pending (OperationLifetime.finalAt receipt.OperationId)
                    )
                )
                Outcome.Succeeded(PresentedLookupResult.NotAccepted(OperationScreen.notAccepted ()))
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            yield
                EndpointValues.output
                    Api.operationGet
                    {
                        OperationReply.OperationId = id
                        Outcome = outcome
                    }

    ]

let defaultReply (endpoint: Endpoint<'Input, 'Output>) =
    replies ()
    |> List.tryPick (EndpointValues.tryOutput endpoint)
    |> Option.defaultWith (fun () -> invalidOp ("No typed behavioral input for " + endpoint.Token))

let samples endpoint =
    replies ()
    |> List.choose (fun reply ->
        reply.Accept
            { new IEndpointOutputVisitor<byte array option> with
                member _.Visit<'input, 'output>(binding: Endpoint<'input, 'output>, value) =
                    if binding.Token = endpoint then
                        Some(ClaimCore.Json.Codec.encode binding.Output value)
                    else
                        None
            })

let authorityReplyBytes () =
    ClaimCore.Json.Codec.encode
        Api.operationExecute.Output
        {
            PresentedExecuteReply.OperationId = OperationId.value authorityReceipt.OperationId
            Outcome =
                PresentedExecuteResult.Accepted(
                    Presented.create ReceiptScreen.view authorityReceipt
                )
        }
