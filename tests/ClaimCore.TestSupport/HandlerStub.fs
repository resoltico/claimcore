module ClaimCore.TestSupport.HandlerStub

open ClaimCore.TestSupport

open ClaimCore.Server

open ClaimCore.Wire
open ClaimCore.Presentation


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let operationId =
    OperationIds.numberedAt (DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)) 4001

let position: LedgerPosition =
    {
        Sequence = Sequence.first
        EntryHash =
            (String.replicate 64 "a"
             |> System.Convert.FromHexString
             |> EntryHash.ofBytes
             |> Option.get)
    }

let private registration =
    DomainInputs.registrationWith
        [
            BusinessField.ClaimantName, "Synthetic claimant"
            BusinessField.InsurerName, "Synthetic insurer"
            BusinessField.ClaimedAmount, "1.00"
        ]
        DomainInputs.registration

let private request reference =
    Submissions.create
        "case.open"
        operationId
        (Binding.CaseNew Revision.zero)
        (Submissions.fields (RegistrationInput.values registration)
         |> Map.add (Submissions.field BusinessField.CaseReference) (SubmissionValue.Text reference))

let commandInput =
    request "WEB-001"
    |> OperationSubmissionCodecs.submission.Write
    |> ClaimCore.Json.WireWriter.bytes

let view reference : CaseSnapshot =
    let reference = CaseFixtures.reference reference

    {
        CaseId = CaseFixtures.caseId operationId
        Reference = reference
        Revision = Revision.first
        State =
            CaseState.Active
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration = DomainInputs.admittedRegistration registration
                    Finance = FinancialPosition.undecided
                }
    }

let reference (request: OperationSubmission) =
    match Binding.caseTarget (Submissions.command request).Binding with
    | Some(CaseSelector.Reference reference) -> reference
    | Some(CaseSelector.Id _) -> "WEB-001"
    | None ->
        match
            Map.tryFind (Submissions.field BusinessField.CaseReference) request.Envelope.Inputs
        with
        | Some(SubmissionValue.Text reference) -> reference
        | _ -> "WEB-001"

let receipt id (reference: string) : OperationReceipt =
    {
        OperationId = CaseFixtures.operationId id
        Position = position
        Subject =
            ReceiptSubject.Case
                {
                    CaseId = CaseId.ofOperationId (CaseFixtures.operationId id)
                    Reference = CaseFixtures.reference reference
                    Revision = Revision.first
                    Act = TaskId.ofLiteral "case.open"
                    Snapshot =
                        SnapshotDisclosure.Disclosed
                            {
                                State = LiveCase.ofState (view reference).State |> Option.get
                                Disposition = None
                                RecordedFields = BusinessField.ofGroup RecordGroup.Registration
                                Payment = None
                            }
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = DomainInputs.actorReference (CaseFixtures.actorId operationId)
        Basis =
            {
                ObservedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
                BusinessDate = DateOnly(2026, 10, 13)
                TimeZoneId = "Etc/UTC"
            }
    }

let peopleReceipt (request: OperationSubmission) =
    let original = receipt request.OperationId "WEB-001"

    { original with
        Subject =
            ReceiptSubject.People
                {
                    TargetActor = PeopleTargetDisclosure.Withheld(CaseFixtures.actorId operationId)
                    Act = request.Task
                    Effects =
                        [
                            {
                                Act = request.Task
                                Id = EffectId.create original.OperationId 0 |> Option.get
                                Position =
                                    EffectPosition.create original.Position.Sequence 0 |> Option.get
                            }
                        ]
                }
    }

// Host tests configure replies rather than implement ledger state or transitions.
type internal ScriptedStore(observed: DateTimeOffset, actor: Actor option, ?reads: IStore) =
    let contexts = System.Collections.Concurrent.ConcurrentQueue<ContextQuery>()

    let facts principal =
        let current = actor |> Option.filter (fun value -> value.Principal = principal)

        {
            GlobalReadFacts.People =
                {
                    Actor = current
                    Installation = if current.IsSome then [ Role.Owner ] else []
                    Cases = Map.empty
                    HasScopedCases = false
                    HasAssignedRoles = current.IsSome
                }
            AssignedRoles =
                if current.IsSome then
                    [
                        {
                            Role = Role.Owner
                            Installation = true
                            CaseCount = 0L
                        }
                    ]
                else
                    []
            Maintenance = RegisterMaintenance.initial
        }

    member _.ContextQueries = contexts.ToArray() |> Array.toList

    interface IStore with
        member _.Context query =
            contexts.Enqueue query

            Task.FromResult(
                Ok
                    {
                        ContextFacts.Facts = facts query.Principal
                        ObservedAt = observed
                    }
            )

        member _.Read operation =
            match reads with
            | Some store -> store.Read operation
            | None -> Task.FromResult(Error StoreFailure.Unavailable)

        member _.Write _ =
            invalidOp "Host reply stubs do not execute writes."

type Stub() =
    let calls = ResizeArray<string>()
    let inputs = ResizeArray<IEndpointInput>()
    member _.Inputs = List.ofSeq inputs
    member _.Calls = List.ofSeq calls
    member val RuntimeAvailable = true with get, set
    member val Error: exn option = None with get, set

    member val Execute: OperationSubmission * CancellationToken -> Task<ExecuteResult> =
        (fun (request, _) ->
            Task.FromResult(
                ExecuteResult.Accepted(receipt request.OperationId (reference request))
            )) with get, set

    member val ExecutePeople: OperationSubmission * CancellationToken -> Task<ExecuteResult> =
        (fun (request, _) -> Task.FromResult(ExecuteResult.Accepted(peopleReceipt request))) with get, set

    member val Principal: PrincipalKey option = None with get, set

    member this.Invoke name result =
        calls.Add name

        match this.Error with
        | Some error -> raise error
        | None -> Task.FromResult result

    member _.InvocationContext principal =
        let store =
            { new IStore with
                member _.Context _ =
                    Task.FromResult(Error StoreFailure.Unavailable)

                member _.Read _ =
                    Task.FromResult(Error StoreFailure.Unavailable)

                member _.Write _ =
                    Task.FromResult(Error StoreFailure.Unavailable)
            }

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        Service.create BuildIdentity.current.Version operationId store calendar principal

    member this.Handlers =
        function
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            Route.represented
                Api.operationPreview
                Api.operationPreviewUnavailable
                HumanReplies.previewSubmission
                (fun _ -> None)
                (fun _ input _ ->
                    let outcome =
                        match input.Operation with
                        | Ok _ ->
                            let result =
                                if
                                    (ClaimCore.Domain.Tasks.Registry.findDeclaration input.Task
                                     |> Option.get)
                                        .Target
                                        =
                                        ActSubject.People
                                then
                                    peopleReceipt input
                                else
                                    receipt input.OperationId (reference input)

                            Outcome.Succeeded(Previewed.AlreadyAccepted result)
                        | Error refusal -> Outcome.Rejected refusal

                    this.Invoke
                        "operation.preview"
                        {
                            PreviewReply.OperationId = input.OperationId
                            Outcome = outcome
                        })
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            Route.represented
                Api.operationExecute
                Api.operationExecuteUnavailable
                (fun _ reply -> HumanReplies.execute reply)
                (fun reply ->
                    match reply.Outcome with
                    | ExecuteResult.Accepted receipt -> Some receipt.RecordedBy.ActorId
                    | _ -> None)
                (fun _ input token ->
                    task {
                        match this.Error with
                        | Some error -> return raise error
                        | None ->
                            calls.Add "operation.execute"

                            let! outcome =
                                match input.Operation with
                                | Ok _ ->
                                    if
                                        (ClaimCore.Domain.Tasks.Registry.findDeclaration input.Task
                                         |> Option.get)
                                            .Target
                                            =
                                            ActSubject.People
                                    then
                                        this.ExecutePeople(input, token)
                                    else
                                        this.Execute(input, token)
                                | Error refusal -> Task.FromResult(ExecuteResult.Rejected refusal)

                            return
                                {
                                    ExecuteReply.OperationId = input.OperationId
                                    Outcome = outcome
                                }

                    })
        | (ActorEndpoint.Query _ | ActorEndpoint.Command _) as endpoint ->
            let definition =
                Api.definitions
                |> List.find (fun definition -> definition.Id = EndpointId.Actor endpoint)

            definition.Accept
                { new IEndpointVisitor<ClaimCore.Server.Route> with
                    member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                        {
                            Description = ClaimCore.Wire.Route.describe endpoint
                            Prepare =
                                fun raw ->
                                    endpoint.Input.Validate "$" raw
                                    |> Result.bind (fun () -> endpoint.Input.Read "$" raw)
                                    |> Result.map (fun input ->
                                        {
                                            OperationId =
                                                endpoint.Correlation
                                                |> Option.map (fun echo -> echo.Request input)
                                            Invoke =
                                                fun representation context _ completed ->
                                                    task {
                                                        let! reply =
                                                            match context with
                                                            | None ->
                                                                Task.FromResult(
                                                                    ResponseExamples.unavailable
                                                                        representation
                                                                        endpoint
                                                                        input
                                                                )
                                                            | Some _ ->
                                                                inputs.Add(
                                                                    EndpointValues.input
                                                                        endpoint
                                                                        input
                                                                )

                                                                this.Invoke
                                                                    endpoint.Token
                                                                    (ResponseExamples
                                                                        .forRepresentation
                                                                        representation
                                                                        endpoint)

                                                        completed ()

                                                        return
                                                            {
                                                                Response =
                                                                    Endpoint.reply endpoint reply
                                                                RecordedBy = None
                                                            }
                                                    }
                                        })
                        }
                }
