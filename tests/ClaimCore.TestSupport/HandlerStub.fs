module ClaimCore.TestSupport.HandlerStub

open ClaimCore.TestSupport

open ClaimCore.Web

open ClaimCore.Protocol


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let operationId = Guid.Parse "40000000-0000-4000-8000-000000000001"

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
    RequestCodecs.operationInput.Write(OperationInput.Case(request "WEB-001"))
    |> ClaimCore.Json.WireWriter.bytes

let view reference =
    CaseFixtures.decide (DateOnly(2026, 10, 13)) (request reference) None
    |> Result.map Case.snapshot
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic case failed admission.")

let receipt id (reference: string) =
    {
        OperationId = CaseFixtures.operationId id
        Position = position
        Subject =
            ReceiptSubject.Case
                {
                    Reference = CaseFixtures.reference reference
                    Revision = Revision.first
                    Act = CaseActKind.Open
                    Snapshot =
                        SnapshotDisclosure.Disclosed(
                            LiveCase.ofState (view reference).State |> Option.get
                        )
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = CaseFixtures.actorId operationId
    }

let authorityReceipt (request: ClaimCore.Domain.AuthorityRequest) =
    { receipt request.OperationId "WEB-001" with
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActorId = CaseFixtures.actorId operationId
                    Act = request.Act |> fun value -> AuthorityAct.kind value
                }
    }


type Stub() =
    let calls = ResizeArray<string>()
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
            new MemoryStore.Store(operationId, DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero))

        store.ReadFailure <- Some StoreFailure.Unavailable
        store.WriteFailure <- Some StoreFailure.Unavailable

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        Service.create
            BuildIdentity.current.Version
            operationId
            (store :> IStore)
            calendar
            principal

    member this.Handlers =
        function
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            Route.bind Api.operationPreview (fun _ input _ ->
                let outcome =
                    match input with
                    | OperationInput.Case request ->
                        (Previewed.AlreadyAccepted >> Outcome.Succeeded) (
                            receipt request.OperationId request.CaseReference
                        )
                    | OperationInput.Authority request ->
                        (Previewed.AlreadyAccepted >> Outcome.Succeeded) (authorityReceipt request)

                this.Invoke
                    "operation.preview"
                    {
                        PreviewReply.OperationId = OperationInput.operationId input
                        Outcome = outcome
                    })
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            Route.bind Api.operationExecute (fun _ input token ->
                task {
                    match this.Error with
                    | Some error -> return raise error
                    | None ->
                        calls.Add "operation.execute"

                        let! outcome =
                            match input with
                            | OperationInput.Case request -> this.Execute(request, token)
                            | OperationInput.Authority request ->
                                this.ExecuteAuthority(request, token)

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
                        let reply = ResponseExamples.defaultReply endpoint
                        Route.bind endpoint (fun _ _ _ -> this.Invoke endpoint.Token reply)
                }
