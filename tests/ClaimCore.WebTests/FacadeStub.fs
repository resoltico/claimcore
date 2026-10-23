module ClaimCore.WebTests.FacadeStub

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
                (CaseAct.Open
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

    Case.decide (DateOnly(2026, 10, 13)) request None
    |> Result.map Case.snapshot
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic case failed admission.")

let receipt id reference =
    {
        OperationId = id
        Position = position
        Scope =
            ReceiptScope.Case
                {
                    Reference = reference
                    Revision = 1L
                    Act = (CaseActKind.Business BusinessActKind.Open)
                    Snapshot = SnapshotDisclosure.Disclosed (view reference).View
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = operationId
    }

let authorityReceipt (request: ClaimCore.Contract.AuthorityRequest) =
    { receipt request.OperationId "WEB-001" with
        Scope =
            ReceiptScope.Authority
                {
                    TargetActorId = operationId
                    Act =
                        ClaimCore.RecordFormat.RecordedAuthorityAct.Operation request.Act
                        |> fun value -> ClaimCore.RecordFormat.RecordedAuthorityActs.kind value
                        |> AuthorityRecordKinds.fromRecord
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

type Facade() =
    let calls = ResizeArray<string>()
    member _.Calls = List.ofSeq calls
    member val Error: exn option = None with get, set

    member val Execute: CaseRequest * CancellationToken -> Task<ExecuteOutcome> =
        (fun (request, _) ->
            Task.FromResult(
                ExecuteOutcome.Accepted(receipt request.OperationId request.CaseReference)
            )) with get, set

    member val ExecuteAuthority: ClaimCore.Contract.AuthorityRequest * CancellationToken
        -> Task<ExecuteOutcome> =
        (fun (request, _) -> Task.FromResult(ExecuteOutcome.Accepted(authorityReceipt request))) with get, set

    member val Principal: PrincipalKey option = None with get, set

    member this.Invoke name result =
        calls.Add name

        match this.Error with
        | Some error -> raise error
        | None -> Task.FromResult result

    member this.ActorCore = this :> IActorClaimsCore

    interface IActorClaimsCore with
        member this.Context _ =
            this.Invoke "context" (ReadReply.Succeeded description)

        member this.Preview(input, _) =
            match input with
            | OperationInput.Case request ->
                this.Invoke
                    "case.preview"
                    (PreviewOutcome.AlreadyAccepted(
                        receipt request.OperationId request.CaseReference
                    ))
            | OperationInput.Authority request ->
                this.Invoke
                    "authority.preview"
                    (PreviewOutcome.AlreadyAccepted(authorityReceipt request))

        member this.Execute(input, token) =
            match this.Error with
            | Some error -> raise error
            | None ->
                match input with
                | OperationInput.Case request ->
                    calls.Add "case.execute"
                    this.Execute(request, token)
                | OperationInput.Authority request ->
                    calls.Add "authority.execute"
                    this.ExecuteAuthority(request, token)

        member this.Get(request, _) =
            let reference = request.CaseReference

            this.Invoke
                "case.get"
                (ReadReply.Succeeded
                    {
                        Snapshot = view reference
                        Advice = { Acts = []; History = [] }
                    })

        member this.List(_, _) =
            this.Invoke
                "case.list"
                (ReadReply.Succeeded
                    {
                        Items = []
                        NextCursor = None
                        Offers = []
                    })

        member this.History(_, _) =
            this.Invoke "case.history" (ReadReply.Succeeded { Entries = []; NextCursor = None })

        member this.Observe(request, _) =
            let id = request.OperationId
            this.Invoke "operation.observe" (ReadReply.Succeeded(receipt id "WEB-001"))

        member this.ListGrants(request, _) =
            let principal =
                PrincipalKey.human "https://identity.example/" "synthetic-target"
                |> Result.defaultWith failwith

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
                    })

        member this.ListActors(_, _) =
            this.Invoke "authority.list" (ReadReply.Succeeded { Items = []; NextCursor = None })
