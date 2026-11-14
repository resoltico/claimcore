module ClaimCore.TestSupport.ResponseExamples

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json

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
                    Finance = FinancialPosition.undecided
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
                        SnapshotDisclosure.Disclosed
                            {
                                State = LiveCase.ofState snapshot.State |> Option.get
                                RecordedFields = BusinessField.ofGroup RecordGroup.Registration
                                Payment = None
                            }
                }
    }

let peopleReceipt =
    { receipt with
        Subject =
            ReceiptSubject.People
                {
                    TargetActor = PeopleTargetDisclosure.Disclosed actor
                    Act = PeopleActKind.ProvisionOwner
                    Effects =
                        [
                            {
                                Act = PeopleActKind.ProvisionOwner
                                Id = EffectId.create receipt.OperationId 0 |> Option.get
                                Position = EffectPosition.create position.Sequence 0 |> Option.get
                            }
                        ]
                }
    }

let caseRead: CaseReadModel =
    {
        Snapshot = snapshot
        LatestPayments = []
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

let previewInput =
    OperationInput.Case
        {
            OperationId = id
            Target = CaseSelector.Id snapshot.CaseId
            ExpectedRevision = 1L
            Act = CaseAct.Void "Synthetic reason"
        }

let preview: PreviewReply =
    {
        OperationId = id
        Outcome =
            Outcome.Succeeded(
                Previewed.Reviewed(
                    OperationReview.Case
                        {
                            Before = Some snapshot
                            Proposed =
                                { snapshot with
                                    Revision = CaseFixtures.revision 2L
                                }
                            Context = runtime
                            Changes =
                                {
                                    Lifecycle = None
                                    Payments = None
                                    Fields =
                                        [
                                            {
                                                FieldName = BusinessField.ClaimantName
                                                Before =
                                                    Some(
                                                        CaseFieldValue.Business(
                                                            Admitted.Name(Inputs.name "Before")
                                                        )
                                                    )
                                                After =
                                                    Some(
                                                        CaseFieldValue.Business(
                                                            Admitted.Name(Inputs.name "After")
                                                        )
                                                    )
                                            }
                                        ]
                                }
                        }
                )
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

let private hostRead (endpoint: Endpoint<'request, Outcome<Presented<'reply>>>) describe success =
    [
        EndpointValues.output endpoint (Presented.outcome describe success)
        EndpointValues.output
            endpoint
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
        EndpointValues.output endpoint (Outcome.Failed CoreFault.StoreUnavailable)
    ]

/// Typed behavioral inputs are independent of the literal decoding examples used by conformance tests.
let private actorRead
    (endpoint: Endpoint<'request, RepresentationReply<HumanReadReply, Outcome<'reply>>>)
    describe
    success
    =
    [
        for outcome in
            [
                success
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            yield EndpointValues.output endpoint (RepresentationReply.Data outcome)

            yield
                EndpointValues.output
                    endpoint
                    (RepresentationReply.Human(HumanReplies.read describe outcome))
    ]

let private replies () : IEndpointOutput list =
    [
        yield!
            hostRead
                Api.session
                (fun (reply: Session) ->
                    ContextScreen.sessionView reply.Authenticated reply.ContractIdentity)
                (Outcome.Succeeded
                    {
                        Session.Authenticated = true
                        ContractIdentity = ContractIdentity.value
                        AntiforgeryToken = Some "csrf"
                    })
        yield! hostRead Api.logout (fun () -> ContextScreen.logoutView ()) (Outcome.Succeeded())
        yield!
            actorRead
                Api.context
                HumanReadDocuments.context
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
                                            Capability.ManageAccess
                                            Capability.ReadAccess
                                        ]
                                    View =
                                        [
                                            ClaimCore.Application.Contracts.View.Cases
                                            ClaimCore.Application.Contracts.View.People
                                            ClaimCore.Application.Contracts.View.Operations
                                        ]
                                    Offers = [ InstallationOffer.Open ]
                                }
                    })
        yield! actorRead Api.caseGet HumanCaseDocument.ofCore (Outcome.Succeeded caseRead)
        yield!
            actorRead
                Api.casePayments
                HumanReadDocuments.payments
                (Outcome.Succeeded
                    {
                        CasePaymentPage.Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = snapshot.Reference
                        Revision = snapshot.Revision
                        WorkRevision = Revision.zero
                        State = CaseState.map _.Finance snapshot.State
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.caseNotes
                HumanReadDocuments.notes
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
            actorRead
                Api.caseList
                HumanReadDocuments.cases
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        Offers = []
                        NextCursor = None
                        ExactReference = None
                    })
        yield!
            actorRead
                Api.caseSummary
                HumanReadDocuments.summary
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
            actorRead
                Api.caseAssignees
                HumanReadDocuments.assignees
                (Outcome.Succeeded
                    {
                        CaseId = snapshot.CaseId
                        WorkRevision = Revision.zero
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.caseTimeline
                HumanReadDocuments.timeline
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
            actorRead
                Api.workViews
                HumanReadDocuments.views
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
            actorRead
                Api.reminder
                HumanReadDocuments.reminder
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
            actorRead
                Api.reminders
                HumanReadDocuments.reminders
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleList
                HumanReadDocuments.actors
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleGrants
                HumanReadDocuments.grants
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Actor = directoryActor
                        Offers = []
                        ProfileSeed = None
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleHistory
                HumanReadDocuments.history
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Entries = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleRequests
                HumanReadDocuments.requests
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead Api.peopleRequestsDismiss HumanReadDocuments.dismissed (Outcome.Succeeded())
        for outcome in
            [
                preview.Outcome
                Outcome.Succeeded(Previewed.AlreadyAccepted receipt)
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            let reply = { preview with Outcome = outcome }
            yield EndpointValues.output Api.operationPreview (RepresentationReply.Data reply)

            yield
                EndpointValues.output
                    Api.operationPreview
                    (RepresentationReply.Human(HumanReplies.preview previewInput reply))
        for outcome in
            [
                ExecuteResult.Accepted receipt
                ExecuteResult.AlreadyAccepted receipt
                ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                ExecuteResult.Failed CoreFault.StoreUnavailable
                ExecuteResult.OutcomeUnknown
            ] do
            let reply =
                {
                    ExecuteReply.OperationId = id
                    Outcome = outcome
                }

            yield EndpointValues.output Api.operationExecute (RepresentationReply.Data reply)

            yield
                EndpointValues.output
                    Api.operationExecute
                    (RepresentationReply.Human(HumanReplies.execute reply))
        for outcome in
            [
                Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true))
                Outcome.Succeeded(
                    OperationLookupResult.Pending(OperationLifetime.finalAt receipt.OperationId)
                )
                Outcome.Succeeded OperationLookupResult.NotAccepted
                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                Outcome.Failed CoreFault.StoreUnavailable
            ] do
            let reply =
                {
                    OperationReply.OperationId = id
                    Outcome = outcome
                }

            yield EndpointValues.output Api.operationGet (RepresentationReply.Data reply)

            yield
                EndpointValues.output
                    Api.operationGet
                    (RepresentationReply.Human(HumanReplies.lookup reply))

    ]

let defaultReply (endpoint: Endpoint<'Input, 'Output>) =
    replies ()
    |> List.tryPick (EndpointValues.tryOutput endpoint)
    |> Option.defaultWith (fun () -> invalidOp ("No typed behavioral input for " + endpoint.Token))

let defaultHuman (endpoint: Endpoint<'input, RepresentationReply<'human, 'data>>) =
    replies ()
    |> List.choose (EndpointValues.tryOutput endpoint)
    |> List.tryFind (function
        | RepresentationReply.Human _ -> true
        | RepresentationReply.Data _ -> false)
    |> Option.defaultWith (fun () -> invalidOp ("No human behavioral input for " + endpoint.Token))

let forRepresentation representation (endpoint: Endpoint<'input, 'output>) =
    replies ()
    |> List.choose (EndpointValues.tryOutput endpoint)
    |> List.find (fun value ->
        (JsonValue.tryMember "representation" (endpoint.Output.Write value)) =
            Some(JsonValue.String(Representation.token representation)))

/// A closed runtime never invokes a canned use case. Retain the requested channel and echo.
let unavailable representation (endpoint: Endpoint<'input, 'output>) input =
    match representation with
    | Representation.Data -> endpoint.Unavailable input
    | Representation.Human ->
        let failed =
            replies ()
            |> List.choose (EndpointValues.tryOutput endpoint)
            |> List.find (fun value ->
                let wire = endpoint.Output.Write value

                (endpoint.Classify value).Kind = ReplyKind.Failed
                && JsonValue.tryMember "representation" wire = Some(JsonValue.String "HUMAN"))

        let wire = endpoint.Output.Write failed

        let correlated =
            match endpoint.Correlation, wire with
            | Some echo, JsonValue.Object fields ->
                JsonValue.Object(
                    fields
                    |> List.map (fun (key, value) ->
                        key,
                        if key = "operationId" then
                            JsonValue.String((echo.Request input).ToString("D"))
                        else
                            value)
                )
            | _ -> wire

        endpoint.Output.Read "$" correlated
        |> Result.defaultWith (fun error -> invalidOp (string error))

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

let peopleReplyBytes () =
    Codec.encode
        Api.operationExecute.Output
        (RepresentationReply.Data
            {
                ExecuteReply.OperationId = OperationId.value peopleReceipt.OperationId
                Outcome = ExecuteResult.Accepted peopleReceipt
            })
