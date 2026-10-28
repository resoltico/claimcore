module ClaimCore.TestSupport.HandlerStub

open ClaimCore.TestSupport

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
        Sequence = Sequence.first
        EntryHash =
            (String.replicate 64 "a"
             |> System.Convert.FromHexString
             |> EntryHash.ofBytes
             |> Option.get)
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
                (AuthoredCaseAct.Open(
                    ClaimCore.TestSupport.Inputs.registrationWith
                        [
                            BusinessField.ClaimantName, ("Synthetic claimant")
                            BusinessField.InsurerName, ("Synthetic insurer")
                            BusinessField.ClaimedAmount, ("1.00")
                        ]
                        (Inputs.registration)
                ))
        }

    CaseFixtures.decide (DateOnly(2026, 10, 13)) request None
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
        | (ActorEndpoint.Query QueryEndpoint.Context) ->
            Route.bind Api.context (fun _ () _ ->
                this.Invoke "context" (Outcome.Succeeded description))
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
        | (ActorEndpoint.Query QueryEndpoint.Resolve) ->
            Route.bind Api.operationResolve (fun _ _ _ ->
                this.Invoke "operation.resolve" (Outcome.Succeeded Resolved.NotAccepted))
        | (ActorEndpoint.Query QueryEndpoint.CaseGet) ->
            Route.bind Api.caseGet (fun _ request _ ->
                this.Invoke
                    "case.get"
                    (Outcome.Succeeded
                        {
                            Snapshot = view request.CaseReference
                            Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                        }))
        | (ActorEndpoint.Query QueryEndpoint.CaseList) ->
            Route.bind Api.caseList (fun _ _ _ ->
                this.Invoke
                    "case.list"
                    (Outcome.Succeeded
                        {
                            Items = []
                            NextCursor = None
                            Offers = []
                        }))
        | (ActorEndpoint.Query QueryEndpoint.CaseHistory) ->
            Route.bind Api.caseHistory (fun _ _ _ ->
                this.Invoke "case.history" (Outcome.Succeeded { Entries = []; NextCursor = None }))
        | (ActorEndpoint.Query QueryEndpoint.Observe) ->
            Route.bind Api.operationObserve (fun _ request _ ->
                this.Invoke
                    "operation.observe"
                    (Outcome.Succeeded(receipt request.OperationId "WEB-001")))
        | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            Route.bind Api.authorityGrants (fun _ request _ ->
                let principal =
                    PrincipalKey.human "https://identity.example/" "synthetic-target"
                    |> Result.defaultWith (string >> failwith)

                this.Invoke
                    "authority.grants"
                    (Outcome.Succeeded
                        {
                            Actor =
                                {
                                    ActorId = CaseFixtures.actorId request.ActorId
                                    Principal = principal
                                    Enabled = false
                                }
                            Items = []
                            NextCursor = None
                        }))
        | (ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            Route.bind Api.authorityList (fun _ _ _ ->
                this.Invoke "authority.list" (Outcome.Succeeded { Items = []; NextCursor = None }))
