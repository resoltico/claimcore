module ClaimCore.TestSupport.HandlerStub

open ClaimCore.Web

open ClaimCore.Protocol


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain

let operationId = Guid.Parse "40000000-0000-4000-8000-000000000001"

let position: LedgerPosition =
    {
        Sequence = 1L
        EntryHash = String.replicate 64 "a"
    }
// Independently authored valid OPEN exercises dispatch as well as shape.
let commandInput =
    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
        {|
            kind = "CASE"
            operationId = operationId.ToString("D")
            caseReference = "WEB-001"
            expectedRevision = 0L
            act =
                {|
                    kind = "OPEN"
                    values =
                        {|
                            incidentDate = "2026-08-01"
                            incidentNotificationDate = "2026-08-03"
                            incidentCountry = "LV"
                            claimantName = "Synthetic claimant"
                            insurerName = "Synthetic insurer"
                            claimedAmount = "1.00"
                            claimedCurrency = "EUR"
                        |}
                |}
        |}

let view reference =
    let request =
        {
            OperationId = operationId
            CaseReference = reference
            ExpectedRevision = 0L
            Act =
                (AuthoredCaseAct.Open
                    { Inputs.registration with
                        ClaimantName = "Synthetic claimant"
                        InsurerName = "Synthetic insurer"
                        ClaimedAmount = "1.00"
                    })
        }

    CaseFixtures.decide (DateOnly(2026, 10, 13)) request None
    |> Result.map Case.snapshot
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic case failed admission.")

let receipt id (reference: string) =
    {
        OperationId = id
        Position = position
        Subject =
            ReceiptSubject.Case
                {
                    Reference = CaseFixtures.reference reference
                    Revision = 1L
                    Act = CaseActKind.Open
                    Snapshot =
                        SnapshotDisclosure.Disclosed(
                            LiveCase.ofState (view reference).State |> Option.get
                        )
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = operationId
    }

let authorityReceipt (request: ClaimCore.Domain.AuthorityRequest) =
    { receipt request.OperationId "WEB-001" with
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActorId = operationId
                    Act = request.Act |> fun value -> AuthorityAct.kind value
                }
    }

let description =
    {
        Runtime =
            {
                ProductVersion = BuildIdentity.current.Version
                EffectiveBusinessDate = DateOnly(2026, 10, 13)
                TimeZoneId = "Etc/UTC"
            }
        View = [ View.Cases; View.Authority; View.Operations ]
        Capability = Capability.all
        Offers = []
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
            new MemoryStore.Store(DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero))

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
        | (ActorEndpoint.Query QueryEndpoint.Context) ->
            Route.read Api.context (fun _ -> None) (fun _ () _ ->
                this.Invoke "context" (ReadReply.Succeeded description))
        | (ActorEndpoint.Operation OperationEndpoint.Preview) ->
            Route.bind
                Api.operationPreview
                (fun input -> Some(OperationInput.operationId input))
                (fun input ->
                    {
                        PreviewReply.OperationId = OperationInput.operationId input
                        Outcome = PreviewResult.Failed CoreFault.StoreUnavailable
                    })
                (fun _ input _ ->
                    let outcome =
                        match input with
                        | OperationInput.Case request ->
                            PreviewResult.AlreadyAccepted(
                                receipt request.OperationId request.CaseReference
                            )
                        | OperationInput.Authority request ->
                            PreviewResult.AlreadyAccepted(authorityReceipt request)

                    this.Invoke
                        "operation.preview"
                        {
                            PreviewReply.OperationId = OperationInput.operationId input
                            Outcome = outcome
                        })
        | (ActorEndpoint.Operation OperationEndpoint.Execute) ->
            Route.bind
                Api.operationExecute
                (fun input -> Some(OperationInput.operationId input))
                (fun input ->
                    {
                        ExecuteReply.OperationId = OperationInput.operationId input
                        Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                    })
                (fun _ input token ->
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
        | (ActorEndpoint.Query QueryEndpoint.Resolve) ->
            Route.bind
                Api.operationResolve
                (fun input -> Some input.OperationId)
                (fun _ -> ResolveReply.Failed CoreFault.StoreUnavailable)
                (fun _ _ _ -> this.Invoke "operation.resolve" ResolveReply.NotAccepted)
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) ->
            Route.read Api.caseGet (fun _ -> None) (fun _ request _ ->
                this.Invoke
                    "case.get"
                    (ReadReply.Succeeded
                        {
                            Snapshot = view request.CaseReference
                            Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                        }))
        | (ActorEndpoint.Query QueryEndpoint.CaseList) ->
            Route.read Api.caseList (fun _ -> None) (fun _ _ _ ->
                this.Invoke
                    "case.list"
                    (ReadReply.Succeeded
                        {
                            Items = []
                            NextCursor = None
                            Offers = []
                        }))
        | (ActorEndpoint.Query QueryEndpoint.CaseHistory) ->
            Route.read Api.caseHistory (fun _ -> None) (fun _ _ _ ->
                this.Invoke
                    "case.history"
                    (ReadReply.Succeeded { Entries = []; NextCursor = None }))
        | (ActorEndpoint.Query QueryEndpoint.Observe) ->
            Route.read
                Api.operationObserve
                (fun input -> Some input.OperationId)
                (fun _ request _ ->
                    this.Invoke
                        "operation.observe"
                        (ReadReply.Succeeded(receipt request.OperationId "WEB-001")))
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            Route.read Api.authorityGrants (fun _ -> None) (fun _ request _ ->
                let principal =
                    PrincipalKey.human "https://identity.example/" "synthetic-target"
                    |> Result.defaultWith (string >> failwith)

                this.Invoke
                    "authority.grants"
                    (ReadReply.Succeeded
                        {
                            Actor =
                                {
                                    ActorId = request.ActorId
                                    Principal = principal
                                    Enabled = false
                                }
                            Items = []
                            NextCursor = None
                        }))
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            Route.read Api.authorityList (fun _ -> None) (fun _ _ _ ->
                this.Invoke
                    "authority.list"
                    (ReadReply.Succeeded { Items = []; NextCursor = None }))
