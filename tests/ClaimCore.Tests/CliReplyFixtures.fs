module ClaimCore.Tests.CliReplyFixtures

open System
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

let id = Guid.Parse "10000000-0000-4000-8000-000000000001"

let caseRequest =
    {
        OperationId = id
        CaseReference = "x"
        ExpectedRevision = 0L
        Act =
            CaseAct.Open
                { Fixtures.registration with
                    IncidentDate = "2026-01-01"
                    ClaimantName = "x"
                }
    }

let authorityRequest =
    {
        OperationId = id
        Target =
            {
                Kind = "HUMAN"
                Issuer = "https://issuer.example/"
                Subject = "synthetic"
            }
        Act = AuthorityAct.RegisterActor
    }

let snapshot =
    let fields: CaseFields =
        {
            IncidentDate = "2026-01-01"
            IncidentNotificationDate = "2026-01-01"
            IncidentCountry = "LV"
            ClaimantName = "x"
            InsurerName = "x"
            ClaimedAmount = "0"
            ClaimedCurrency = "EUR"
            CaseReference = "x"
            PaymentDecisionDate = None
            PayableAmount = None
            PayableCurrency = None
            PaymentDate = None
            Status = CaseStatus.Opened
        }

    {
        Reference = "x"
        Revision = 1L
        View = CaseView.Live(fields, Disposition.Active)
    }

let receipt =
    {
        OperationId = id
        Position =
            {
                Sequence = 1L
                EntryHash = String.replicate 64 "0"
            }
        Scope =
            ReceiptScope.Case
                {
                    Reference = "x"
                    Revision = 1L
                    Act = CaseActKind.Business BusinessActKind.Open
                    Snapshot = SnapshotDisclosure.Withheld
                }
        RecordedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = id
    }

let review =
    OperationReview.Case
        {
            Before = None
            Proposed = snapshot
            Changes = []
            Context =
                {
                    ProductVersion = "synthetic"
                    EffectiveBusinessDate = DateOnly(2026, 1, 1)
                    TimeZoneId = "Etc/UTC"
                }
        }

let private resourceInput endpoint =
    match endpoint with
    | "session"
    | "session.logout"
    | "context" -> Api.context.Input.Write()
    | "case.get" -> Api.caseGet.Input.Write { CaseReference = "x" }
    | "case.list"
    | "authority.list" -> Api.caseList.Input.Write { AfterCursor = None; Limit = 1 }
    | "case.history" ->
        Api.caseHistory.Input.Write
            {
                CaseReference = "x"
                AfterCursor = None
                Limit = 1
                Detail = HistoryDetail.Full
            }
    | "operation.execute"
    | "operation.preview" -> Api.operationExecute.Input.Write(OperationInput.Case caseRequest)
    | "operation.observe" -> Api.operationObserve.Input.Write { OperationId = id }
    | "authority.grants" ->
        Api.authorityGrants.Input.Write
            {
                ActorId = id
                AfterCursor = None
                Limit = 1
            }
    | _ -> invalidArg (nameof endpoint) "No synthetic request for this endpoint."

let input endpoint =
    match endpoint with
    | "operation.resolve" -> Api.operationResolve.Input.Write { OperationId = id }
    | _ -> resourceInput endpoint

let private reads codec data =
    [
        ReadReply.Succeeded data
        ReadReply.Rejected Refusal.InvalidCursor
        ReadReply.Failed CoreFault.StoreUnavailable
    ]
    |> List.map codec.Write

let private execute () =
    [
        ExecuteResult.Accepted receipt
        ExecuteResult.AlreadyAccepted receipt
        ExecuteResult.Rejected Refusal.ResourceUnavailable
        ExecuteResult.Failed CoreFault.StoreUnavailable
        ExecuteResult.OutcomeUnknown
    ]
    |> List.map (fun outcome ->
        Api.operationExecute.Output.Write
            {

                OperationId = id
                Outcome = outcome
            })

let private preview () =
    [
        PreviewResult.Reviewed review
        PreviewResult.AlreadyAccepted receipt
        PreviewResult.Rejected Refusal.ResourceUnavailable
        PreviewResult.Failed CoreFault.StoreUnavailable
    ]
    |> List.map (fun outcome ->
        Api.operationPreview.Output.Write { OperationId = id; Outcome = outcome })

let private context =
    {
        Runtime =
            {
                ProductVersion = "synthetic"
                EffectiveBusinessDate = DateOnly(2026, 1, 1)
                TimeZoneId = "Etc/UTC"
            }
        Capabilities = []
        Views = []
        Offers = []
    }

let private grants =
    {
        Actor =
            {
                ActorId = id
                Principal = authorityRequest.Target
                Enabled = true
            }
        Items = []
        NextCursor = None
    }

let private resourceResponses endpoint =
    match endpoint with
    | "session" ->
        reads
            Api.session.Output
            {
                Authenticated = false
                ContractIdentity = ContractIdentity.value
                AntiforgeryToken = None
            }
    | "session.logout" -> reads Api.logout.Output ()
    | "context" -> reads Api.context.Output context
    | "case.get" ->
        reads
            Api.caseGet.Output
            {
                Snapshot = snapshot
                Advice = { Acts = []; History = [] }
            }
    | "case.list" ->
        reads
            Api.caseList.Output
            {
                Items = []
                Offers = []
                NextCursor = None
            }
    | "case.history" -> reads Api.caseHistory.Output { Entries = []; NextCursor = None }
    | "operation.execute" -> execute ()
    | "operation.preview" -> preview ()
    | "operation.observe" -> reads Api.operationObserve.Output receipt
    | "authority.list" -> reads Api.authorityList.Output { Items = []; NextCursor = None }
    | "authority.grants" -> reads Api.authorityGrants.Output grants
    | _ -> invalidArg (nameof endpoint) "No synthetic response for this endpoint."

let responses endpoint =
    match endpoint with
    | "operation.resolve" ->
        [
            ResolveReply.Accepted receipt
            ResolveReply.NotAccepted
            ResolveReply.Rejected Refusal.ResourceUnavailable
            ResolveReply.Failed CoreFault.StoreUnavailable
        ]
        |> List.map Api.operationResolve.Output.Write
    | _ -> resourceResponses endpoint
    |> List.map WireWriter.bytes
