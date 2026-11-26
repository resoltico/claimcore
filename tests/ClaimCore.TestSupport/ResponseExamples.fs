module ClaimCore.TestSupport.ResponseExamples

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json

let private id = OperationIds.numberedAt DomainInputs.workBasis.ObservedAt 1
let private actorId = CaseFixtures.actorId id
let private actor = DomainInputs.namedActorReference actorId "Synthetic recorder"
let private reference = DomainInputs.reference "CASE-1"
let private basis = DomainInputs.workBasis

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
                    Handling = Handling.Open
                    Registration = DomainInputs.admittedRegistration DomainInputs.registration
                    Finance = FinancialPosition.undecided
                }
    }

let receipt: OperationReceipt =
    {
        OperationId = CaseFixtures.operationId id
        Position = position
        RecordedAt = basis.ObservedAt
        RecordedBy = AuthorityRef.Actor actor
        Basis = basis
        Subject =
            ReceiptSubject.Case
                {
                    CaseId = snapshot.CaseId
                    Reference = reference
                    Revision = Revision.first
                    Act = (TaskId.ofLiteral "case.open")
                    Snapshot =
                        SnapshotDisclosure.Disclosed
                            {
                                State = LiveCase.ofState snapshot.State |> Option.get
                                Disposition = None
                                ReopenReason = None
                                RecordedFields = BusinessField.ofGroup RecordGroup.Registration
                                Payments = []
                            }
                }
    }

let peopleReceipt =
    { receipt with
        RecordedBy = AuthorityRef.Operator
        Subject =
            ReceiptSubject.People
                {
                    TargetActor = PeopleTargetDisclosure.Disclosed actor
                    Act = (TaskId.ofLiteral "people.provisionOwner")
                    Effects =
                        [
                            {
                                Act = (TaskId.ofLiteral "people.provisionOwner")
                                Id = EffectId.create receipt.OperationId 0 |> Option.get
                                Position = EffectPosition.create position.Sequence 0 |> Option.get
                            }
                        ]
                }
    }

let caseRead: CaseReadModel =
    {
        MatchedReference = None
        RelatedCases = RelatedCaseLinks.empty
        FamilySequence = Sequence.create 1L |> Option.get
        Snapshot = snapshot
        Parties = []
        PartyOffers = []
        LatestPayments = []
        PaymentPayees = []
        Advice = Inputs.caseAdvice snapshot.State
        Mine = false
        Work = Inputs.workView snapshot.State
        Headline = CaseHeadline.Undecided
        Attention = []
        Stages = DomainInputs.stageDisplay snapshot.State
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
    Submissions.create
        "case.void"
        id
        (Binding.Case(CaseSelector.Id snapshot.CaseId, Revision.first))
        (Map.ofList
            [
                Submissions.input InputTarget.Reason, SubmissionValue.Text "Synthetic reason"
            ])

let preview: PreviewReply =
    {
        OperationId = id
        Outcome =
            Outcome.Succeeded(
                Previewed.Reviewed
                    {
                        OperationReview.ChangesOwnSignInIdentity = false
                        Task = TaskId.ofLiteral "case.void"
                        Opening = None
                        Target = ReviewTarget.Case(snapshot.CaseId, reference)
                        References = { Actors = []; Cases = [] }
                        Binding = Binding.Case(CaseSelector.Id snapshot.CaseId, Revision.first)
                        Context = runtime
                        Changes =
                            [
                                {
                                    DescribedPart.PartId = CaseId.value snapshot.CaseId
                                    Revision = CaseFixtures.revision 2L
                                    Description =
                                        ClaimCore.Domain.Changes.PartDescription.Case
                                            {
                                                Values =
                                                    {
                                                        Before = CaseState.fields snapshot.State
                                                        After = CaseState.fields snapshot.State
                                                    }
                                                Lifecycle =
                                                    Some
                                                        {
                                                            Before = Some Lifecycle.Active
                                                            After = Some Lifecycle.Voided
                                                        }
                                                Fields = []
                                                Money = []
                                                Disposition =
                                                    Some(
                                                        CaseDisposition.Voided(
                                                            Reason.parse "Synthetic reason"
                                                            |> Result.toOption
                                                            |> Option.get
                                                        )
                                                    )
                                            }
                                }
                            ]
                    }
            )
    }

let private reminder =
    Reminder.decode
        (ReminderId.ofOperationId receipt.OperationId)
        snapshot.CaseId
        Revision.first
        (ReminderAction.parse "Contact claimant"
         |> Result.defaultWith (string >> invalidOp))
        (CalendarDate.ofDate basis.BusinessDate)
        ReminderKind.FollowUp
        ReminderState.Pending
        None
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

let internal replies () : IEndpointOutput list =
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
        yield!
            hostRead
                Api.logout
                (fun _ -> ContextScreen.logoutView ())
                (Outcome.Succeeded(LogoutHandoff.Provider "https://issuer.example.test/logout"))
        yield!
            actorRead
                Api.context
                ContextScreen.document
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
                                    Offers =
                                        [
                                            InstallationOffer.Open(
                                                ClaimCore.Domain.Tasks.Case.Open.editableInputs
                                                    CaseReferencePolicy.CustomAllowed
                                            )
                                        ]
                                }
                    })
        yield! actorRead Api.caseGet HumanCaseDocument.ofCore (Outcome.Succeeded caseRead)
        yield!
            actorRead
                Api.casePayments
                PaymentsScreen.document
                (Outcome.Succeeded
                    {
                        CasePaymentPage.Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = snapshot.Reference
                        Revision = snapshot.Revision
                        WorkRevision = Revision.zero
                        State =
                            CaseState.map
                                (fun (fields: CaseFields) -> fields.Finance)
                                snapshot.State
                        Items = []
                        Payees = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.caseAttachments
                AttachmentsScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = snapshot.Reference
                        Disclosure = Sequence.first
                        Items = []
                        CanAdd = false
                    })
        yield!
            actorRead
                Api.caseNotes
                NotesScreen.document
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
                CaseListScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        Offers = []
                        NextCursor = None
                        ExactReference = None
                        AuthorizedScopeEmpty = true
                    })
        yield!
            actorRead
                Api.caseSummary
                CaseTotalsScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        GroupBy = None
                        Rows =
                            [
                                {
                                    Group = None
                                    Count = 0L
                                    Currency = None
                                    Claimed = None
                                    Payable = None
                                    NetPaid = None
                                }
                            ]
                        NextCursor = None
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
                AssigneesScreen.document
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
                Api.caseParties
                PartiesScreen.document
                (Outcome.Succeeded
                    {
                        PartyChoicePage.Basis = basis
                        Items =
                            [
                                {
                                    PartyId =
                                        PartyId.create (
                                            Guid.Parse "30000000-0000-7000-8000-000000000001"
                                        )
                                        |> Option.get
                                    Name = PartyName.parse "Synthetic claimant" |> Result.toOption
                                    Role = PartyRole.Claimant
                                }
                            ]
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.caseTimeline
                TimelineScreen.document
                (Outcome.Succeeded
                    {
                        Disclosure = HistoryDisclosure.Full
                        AttachmentDisclosure = Sequence.zero
                        Basis = basis
                        CaseId = snapshot.CaseId
                        Reference = reference
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.workViews
                WorkViewsScreen.document
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
                ReminderScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Item =
                            {
                                Reminder = reminder
                                Reference = reference
                                Claimants = []
                                Responsible = None
                                RecordedAt = basis.ObservedAt
                                RecordedBy = actor
                                Offers = []
                                Active = true
                                Timing = Some ReminderDue.Today
                            }
                    })
        yield!
            actorRead
                Api.reminders
                (ReminderListScreen.document (
                    {
                        View = None
                        Target = None
                        State = None
                        Kind = None
                        DeadlineOutcome = None
                        Due = None
                        Assignment = None
                        Text = None
                        AfterCursor = None
                        Limit = 50
                        IncludeInactive = None
                    }
                ))
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleList
                PeopleScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleGrants
                PersonScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Actor = directoryActor
                        Revision = Revision.first
                        Controls = []
                        Offers = []
                        ProfileSeed = None
                        CanTerminateSessions = false
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleHistory
                AccessHistoryScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Entries = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleRequests
                AccessRequestsScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items = []
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.disclosureTrail
                DisclosureTrailScreen.document
                (Outcome.Succeeded
                    {
                        Basis = basis
                        Items =
                            [
                                {
                                    Id = 1L
                                    RecordedAt = basis.ObservedAt
                                    Event =
                                        {
                                            ActorId = actor.ActorId
                                            Kind = DisclosureKind.TrailRead
                                            CaseId = None
                                            AttachmentId = None
                                            Outcome = DisclosureOutcome.Succeeded
                                            Export = None
                                        }
                                }
                            ]
                        NextCursor = None
                    })
        yield!
            actorRead
                Api.peopleRequestsDismiss
                AccessRequestsScreen.dismissedDocument
                (Outcome.Succeeded())
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
                    (RepresentationReply.Human(HumanReplies.previewSubmission previewInput reply))
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

        for outcome in
            [
                SessionTerminationResult.Succeeded { Count = 2L; Self = true }
                SessionTerminationResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                SessionTerminationResult.Failed CoreFault.StoreUnavailable
                SessionTerminationResult.OutcomeUnknown
            ] do
            yield EndpointValues.output Api.terminateOwnSessions (RepresentationReply.Data outcome)

            yield
                EndpointValues.output
                    Api.terminateOwnSessions
                    (RepresentationReply.Human(HumanReplies.sessionTermination outcome))

            yield
                EndpointValues.output Api.terminatePersonSessions (RepresentationReply.Data outcome)

            yield
                EndpointValues.output
                    Api.terminatePersonSessions
                    (RepresentationReply.Human(HumanReplies.sessionTermination outcome))

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

                (endpoint.Classify value) = ReplyKind.Failed
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
