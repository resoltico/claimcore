module ClaimCore.TestSupport.HandlerStub

open ClaimCore.TestSupport

open ClaimCore.Web

open ClaimCore.Wire
open ClaimCore.Presentation


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
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
    Inputs.registrationWith
        [
            BusinessField.ClaimantName, "Synthetic claimant"
            BusinessField.InsurerName, "Synthetic insurer"
            BusinessField.ClaimedAmount, "1.00"
        ]
        Inputs.registration

let private request reference =
    {
        OperationId = operationId
        CaseReference = reference
        ExpectedRevision = 0L
        Act = AuthoredCaseAct.Open registration
    }

let commandInput =
    RequestCodecs.operationInput.Write(
        ClaimCore.TestSupport.Inputs.caseOperation (request "WEB-001")
    )
    |> ClaimCore.Json.WireWriter.bytes

let view reference : CaseSnapshot =
    CaseFixtures.decide
        (DateOnly(2026, 10, 13))
        CasePaymentContext.NoneRequired
        (request reference)
        None
    |> Result.map _.After
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic case failed admission.")

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
                    Act = CaseActKind.Open
                    Snapshot =
                        SnapshotDisclosure.Disclosed
                            {
                                State = LiveCase.ofState (view reference).State |> Option.get
                                RecordedFields = BusinessField.ofGroup RecordGroup.Registration
                                Payment = None
                            }
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = Inputs.actorReference (CaseFixtures.actorId operationId)
        Basis =
            {
                ObservedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
                BusinessDate = DateOnly(2026, 10, 13)
                TimeZoneId = "Etc/UTC"
            }
    }

let authorityReceipt (request: ClaimCore.Domain.AuthorityRequest) =
    let original = receipt request.OperationId "WEB-001"
    let kind = AuthorityAct.kind request.Act

    let kinds =
        match request.Act with
        | AuthorityAct.Register _ -> [ AuthorityKind.Register; AuthorityKind.SetProfile ]
        | AuthorityAct.ProvisionOwner _ ->
            [ AuthorityKind.ProvisionOwner; AuthorityKind.SetProfile ]
        | AuthorityAct.Admit value ->
            AuthorityKind.Register
            :: AuthorityKind.SetProfile
            :: List.replicate value.Grants.Length AuthorityKind.Grant
        | _ -> [ kind ]

    let effects =
        kinds
        |> List.mapi (fun index kind ->
            {
                AuthorityReceiptEffect.Act = kind
                Id = EffectId.create original.OperationId index |> Option.get
                Position = EffectPosition.create original.Position.Sequence index |> Option.get
            })

    { original with
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActor =
                        AuthorityTargetDisclosure.Withheld(CaseFixtures.actorId operationId)
                    Act = kind
                    Effects = effects
                }
    }


// Host tests configure replies rather than implement ledger state or transitions.
type ScriptedRead() =
    inherit System.Reflection.DispatchProxy()

    member val Reply: string -> (obj | null) array -> (obj | null) =
        (fun name _ -> invalidOp ("Unexpected store read: " + name)) with get, set

    override this.Invoke(methodInfo, arguments) =
        this.Reply (nonNull methodInfo).Name (nonNull arguments)

type ScriptedStore(observed: DateTimeOffset, actor: Actor option) =
    let contexts = System.Collections.Concurrent.ConcurrentQueue<ContextQuery>()

    let facts principal =
        let current = actor |> Option.filter (fun value -> value.Principal = principal)

        {
            GlobalReadFacts.Authority =
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

    let reader = System.Reflection.DispatchProxy.Create<IReadTx, ScriptedRead>()

    do
        (reader :?> ScriptedRead).Reply <-
            fun name arguments ->
                match name with
                | "get_ObservedAt" -> box observed
                | "Maintenance" -> box (Task.FromResult RegisterMaintenance.initial)
                | "ExpiredBefore" -> box (Task.FromResult<DateTimeOffset option> None)
                | "GlobalReadFacts" -> box (Task.FromResult(facts (unbox (nonNull arguments[0]))))
                | "Authority" ->
                    box (
                        Task.FromResult
                            (facts (unbox<AuthorityQuery>(nonNull arguments[0])).Principal)
                                .Authority
                    )
                | "FindOperation" ->
                    box (Task.FromResult<ClaimCore.RecordFormat.RetainedOperation option> None)
                | _ -> invalidOp ("Unexpected store read: " + name)

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
            task {
                let! reply = operation.Work reader
                return Ok reply
            }

        member _.Write _ =
            invalidOp "Host reply stubs do not execute writes."

type Stub() =
    let calls = ResizeArray<string>()
    let inputs = ResizeArray<IEndpointInput>()
    member _.Inputs = List.ofSeq inputs
    member _.Calls = List.ofSeq calls
    member val RuntimeAvailable = true with get, set
    member val Error: exn option = None with get, set

    member val Execute: UnvalidatedCaseRequest * CancellationToken -> Task<ExecuteResult> =
        (fun (request, _) ->
            Task.FromResult(
                ExecuteResult.Accepted(receipt request.OperationId request.CaseReference)
            )) with get, set

    member val ExecuteAuthority: ClaimCore.Domain.AuthorityRequest * CancellationToken
        -> Task<ExecuteResult> =
        (fun (request, _) -> Task.FromResult(ExecuteResult.Accepted(authorityReceipt request))) with get, set

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
                HumanReplies.preview
                (fun _ -> None)
                (fun _ input _ ->
                    let outcome =
                        match input with
                        | OperationInput.Case input ->
                            let request = Inputs.referencedCase input

                            (Previewed.AlreadyAccepted >> Outcome.Succeeded) (
                                receipt request.OperationId request.CaseReference
                            )
                        | OperationInput.Authority request ->
                            (Previewed.AlreadyAccepted >> Outcome.Succeeded) (
                                authorityReceipt request
                            )
                        | OperationInput.Note _
                        | OperationInput.Work _
                        | OperationInput.Reminder _ -> Outcome.Failed CoreFault.InternalError

                    this.Invoke
                        "operation.preview"
                        {
                            PreviewReply.OperationId = OperationInput.operationId input
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
                                match input with
                                | OperationInput.Case request ->
                                    this.Execute(Inputs.referencedCase request, token)
                                | OperationInput.Authority request ->
                                    this.ExecuteAuthority(request, token)
                                | OperationInput.Note _
                                | OperationInput.Work _
                                | OperationInput.Reminder _ ->
                                    Task.FromResult(ExecuteResult.Failed CoreFault.InternalError)

                            return
                                {
                                    ExecuteReply.OperationId = OperationInput.operationId input
                                    Outcome = outcome
                                }

                    })
        | ActorEndpoint.Query query ->
            let definition =
                Api.definitions
                |> List.find (fun endpoint ->
                    endpoint.Id = EndpointId.Actor(ActorEndpoint.Query query))

            definition.Accept
                { new IEndpointVisitor<ClaimCore.Web.Route> with
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
