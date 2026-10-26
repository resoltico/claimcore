module ClaimCore.TestSupport.HandlerStub

open ClaimCore.Contract

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
                (UnvalidatedCaseAct.Open
                    {
                        IncidentDate = "2026-08-01"
                        IncidentNotificationDate = "2026-08-03"
                        IncidentCountry = "LV"
                        ClaimantName = "Synthetic claimant"
                        InsurerName = "Synthetic insurer"
                        ClaimedAmount = "1.00"
                        ClaimedCurrency = "EUR"
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
                    Act = (CaseActKind.Business BusinessActKind.Open)
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
                    Act =
                        ClaimCore.RecordFormat.RecordedAuthorityAct.Operation request.Act
                        |> fun value -> ClaimCore.RecordFormat.RecordedAuthorityActs.kind value
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
        Views = [ View.Cases; View.Authority; View.Operations ]
        Capabilities = Capabilities.all
        Offers = []
    }

type Stub() =
    let calls = ResizeArray<string>()
    member _.Calls = List.ofSeq calls
    member val AdmissionOpen = true with get, set
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

        Handlers.context operationId (store :> IStore) calendar principal

    member this.Handlers =
        function
        | ActorEndpoint.Context ->
            Route.read Api.context (fun _ () _ ->
                this.Invoke "context" (ReadReply.Succeeded description))
        | ActorEndpoint.Preview ->
            Route.bind
                Api.operationPreview
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
        | ActorEndpoint.Execute ->
            Route.bind
                Api.operationExecute
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
        | ActorEndpoint.Resolve ->
            Route.bind
                Api.operationResolve
                (fun _ -> ResolveReply.Failed CoreFault.StoreUnavailable)
                (fun _ _ _ -> this.Invoke "operation.resolve" ResolveReply.NotAccepted)
        | ActorEndpoint.CaseGet ->
            Route.read Api.caseGet (fun _ request _ ->
                this.Invoke
                    "case.get"
                    (ReadReply.Succeeded
                        {
                            Snapshot = view request.CaseReference
                            Advice = { Acts = []; History = [] }
                        }))
        | ActorEndpoint.CaseList ->
            Route.read Api.caseList (fun _ _ _ ->
                this.Invoke
                    "case.list"
                    (ReadReply.Succeeded
                        {
                            Items = []
                            NextCursor = None
                            Offers = []
                        }))
        | ActorEndpoint.CaseHistory ->
            Route.read Api.caseHistory (fun _ _ _ ->
                this.Invoke
                    "case.history"
                    (ReadReply.Succeeded { Entries = []; NextCursor = None }))
        | ActorEndpoint.Observe ->
            Route.read Api.operationObserve (fun _ request _ ->
                this.Invoke
                    "operation.observe"
                    (ReadReply.Succeeded(receipt request.OperationId "WEB-001")))
        | ActorEndpoint.AuthorityGrants ->
            Route.read Api.authorityGrants (fun _ request _ ->
                let principal =
                    PrincipalKey.human "https://identity.example/" "synthetic-target"
                    |> Result.defaultWith (AuthorityInputFailures.token >> failwith)

                this.Invoke
                    "authority.grants"
                    (ReadReply.Succeeded
                        {
                            Actor =
                                {
                                    ActorId = request.ActorId
                                    Principal = PrincipalKey.describe principal
                                    Enabled = false
                                }
                            Items = []
                            NextCursor = None
                        }))
        | ActorEndpoint.AuthorityList ->
            Route.read Api.authorityList (fun _ _ _ ->
                this.Invoke
                    "authority.list"
                    (ReadReply.Succeeded { Items = []; NextCursor = None }))
