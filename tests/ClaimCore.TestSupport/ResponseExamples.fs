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

let presentReceipt = Presented.create (Descriptor.receipt >> Descriptor.recordView)

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
                        View = Descriptor.recordView []
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
                    Descriptor.session reply.Authenticated reply.ContractIdentity
                    |> Descriptor.recordView)
                (Outcome.Succeeded
                    {
                        Session.Authenticated = true
                        ContractIdentity = ContractIdentity.value
                        AntiforgeryToken = Some "csrf"
                    })
        yield!
            read
                Api.logout
                (fun () -> Descriptor.logout () |> Descriptor.recordView)
                (Outcome.Succeeded())
        yield!
            read
                Api.context
                (Descriptor.context >> Descriptor.recordView)
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
        yield!
            read
                Api.caseGet
                (Descriptor.caseView >> Descriptor.recordView)
                (Outcome.Succeeded caseRead)
        yield!
            read
                Api.caseList
                (fun (reply: CaseSummaryPage) ->
                    Descriptor.tableView Descriptor.caseSummaryColumns reply.Items)
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
                (Descriptor.caseTotals >> Descriptor.recordView)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Claimed = []
                        Payable = []
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
                (fun (reply: AssigneePage) ->
                    Descriptor.tableView Descriptor.assigneeColumns reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.caseTimeline
                (fun (reply: CaseTimelinePage) ->
                    Descriptor.tableView Descriptor.timelineColumns reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = reference
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.workViews
                (Descriptor.workViews >> Descriptor.recordView)
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
                (fun (reply: ReminderRead) ->
                    Descriptor.reminder reply.Item |> Descriptor.recordView)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Item =
                            {
                                Reminder = reminder
                                Reference = reference
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
                (fun (reply: ReminderPage) ->
                    Descriptor.tableView Descriptor.reminderColumns reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityList
                (fun (reply: ActorReviewPage) ->
                    Descriptor.tableView Descriptor.actorColumns reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityGrants
                (fun (reply: AuthorityGrantPage) ->
                    Descriptor.tableView Descriptor.grantColumns reply.Items)
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
                (fun (reply: AuthorityHistoryPage) ->
                    Descriptor.tableView Descriptor.authorityHistoryColumns reply.Entries)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Entries = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityRequests
                (fun (reply: AccessRequestPage) ->
                    Descriptor.tableView Descriptor.accessRequestColumns reply.Items)
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            read
                Api.authorityRequestsDismiss
                (fun () -> Descriptor.recordView [])
                (Outcome.Succeeded())
        for outcome in
            [
                preview.Outcome
                Outcome.Succeeded(
                    PresentedPreviewed.AlreadyAccepted(
                        Presented.create (Descriptor.receipt >> Descriptor.recordView) receipt
                    )
                )
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            yield EndpointValues.output Api.operationPreview { preview with Outcome = outcome }
        for outcome in
            [
                PresentedExecuteResult.Accepted(
                    Presented.create (Descriptor.receipt >> Descriptor.recordView) receipt
                )
                PresentedExecuteResult.AlreadyAccepted(
                    Presented.create (Descriptor.receipt >> Descriptor.recordView) receipt
                )
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
                        Presented.create (Descriptor.receipt >> Descriptor.recordView) receipt,
                        true
                    )
                )
                Outcome.Succeeded(
                    PresentedLookupResult.Pending(OperationLifetime.finalAt receipt.OperationId)
                )
                Outcome.Succeeded PresentedLookupResult.NotAccepted
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
                    Presented.create (Descriptor.receipt >> Descriptor.recordView) authorityReceipt
                )
        }
