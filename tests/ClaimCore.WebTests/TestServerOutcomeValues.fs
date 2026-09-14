module ClaimCore.WebTests.TestServerOutcomeValues

open System
open System.Net.Http
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.TestServerFixture

let caseReference = "WEB-V2-001"
let timestamp = DateTimeOffset(2026, 9, 9, 10, 11, 12, TimeSpan.Zero)
let attemptId = Guid.Parse("50000000-0000-4000-8000-000000000001")

let fields =
    {
        IncidentDate = "2026-09-01"
        IncidentNotificationDate = "2026-09-02"
        IncidentCountry = "Latvia"
        ClaimantName = "Synthetic claimant"
        InsurerName = "Synthetic insurer"
        ClaimedAmount = "12.34"
        ClaimedCurrency = "EUR"
        CaseReference = caseReference
        PaymentDecisionDate = None
        PayableAmount = None
        PayableCurrency = None
        PaymentDate = None
        Status = CaseStatus.Opened
    }

let snapshot = { Fields = fields; Version = 1L }

let current =
    {
        Record = snapshot
        AvailableCommands = [ CommandKind.Decide; CommandKind.Close ]
    }

let receipt =
    {
        OperationId = operationId
        Snapshot = snapshot
        RecordedAt = timestamp
        RecordedBy = "synthetic-operator"
        Replayed = false
        Command = CommandKind.Open
    }

let rejection: Rejection =
    {
        Code = RejectionCode.VersionConflict
        Message = "Synthetic version conflict."
        Field = None
        ActualVersion = Some 2L
        Action = RecommendedAction.ReadCurrent
    }

let fault: CoreFault =
    {
        Code = FaultCode.StoreUnavailable
        Message = "Synthetic safe store failure."
        Action = RecommendedAction.RetrySafe
    }

let recoveryRejection: RecoveryRejection =
    {
        Code = RecoveryRejectionCode.PreparationDismissed
        Message = "Synthetic preparation is dismissed."
        Action = RecommendedAction.ReadCurrent
    }

let preparationSummary =
    {
        OperationId = operationId
        CaseReference = caseReference
        Command = CommandKind.Open
        PreparedAt = timestamp
        State = PreparationState.Unsubmitted
        Authority = RecoveryAuthority.PendingAuthority
        RequestSha256 = Some digest
        AvailableActions = [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
    }

let preparationDetails =
    {
        Summary = preparationSummary
        ExpectedVersion = 0L
        AuthoredValues = [ "claimantName", fields.ClaimantName ]
        CanonicalCommandFormat = 2
        PreparingApplicationVersion = "0.1.0"
        PreparingContractFingerprint = digest
        PreparingContractKind = "SEMANTIC_CORE_V1"
        Attempts =
            {
                Items = []
                NextCursor = None
                LegacyUncertainty = false
            }
    }

let recoveryDetails =
    {
        Preparation = preparationDetails
        Observation = Lookup.NotFound operationId
    }

let review =
    {
        Before = None
        Proposed = snapshot
        Changes =
            [
                {
                    FieldName = "claimantName"
                    Before = None
                    After = Some fields.ClaimantName
                }
            ]
        Context =
            {
                ProductVersion = "0.1.0"
                EffectiveBusinessDate = DateOnly(2026, 9, 9)
                TimeZoneId = "Etc/UTC"
            }
        IsAdvisory = true
    }

let importPreview =
    {
        ArtifactKind = RecoveryArtifactKind.Envelope
        SourceSha256 = digest
        DecodedEffect =
            {
                OperationId = operationId
                CaseReference = caseReference
                Command = CommandKind.Open
                ExpectedVersion = 0L
                AuthoredValues = [ "claimantName", fields.ClaimantName ]
                CanonicalCommandFormat = 2
                RequestSha256 = digest
            }
        ExistingPreparation = Some preparationSummary
    }

let authenticated (host: Host) =
    Expect.equal (host.Login()).Status 200 "Synthetic TestServer login"
    host.SessionToken()

let postJson (host: Host) token endpoint body =
    host.Send(
        HttpMethod.Post,
        WebContract.jsonPath endpoint,
        Some body,
        Some "application/json",
        Some token
    )

let postRaw (host: Host) token endpoint body =
    let path, mediaType, _, headers = WebContract.raw endpoint
    let digestHeaders = headers |> List.map (fun name -> name, digest)

    host.SendWithHeaders(
        HttpMethod.Post,
        path,
        Some body,
        Some mediaType,
        Some token,
        digestHeaders
    )

let tagged endpoint tag (reply: Reply) =
    Expect.equal reply.Status 200 "Business outcomes are typed HTTP 200 bodies"
    use body = document reply
    let root = body.RootElement
    Expect.equal (root.GetProperty("endpoint").GetString()) endpoint "Generated endpoint identity"
    let outcome = root.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("tag").GetString()) tag "Exact typed outcome tag"
    outcome.GetProperty("data").Clone()
