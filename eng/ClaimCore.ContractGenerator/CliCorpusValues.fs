namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal CliEncodedSample =
    {
        Identifier: string
        Endpoint: string option
        Response: CliWireResponse
    }

[<RequireQualifiedAccess>]
module internal CliCorpusValues =
    let sample identifier endpoint response =
        {
            Identifier = identifier
            Endpoint = Some endpoint
            Response = response
        }

    let operationId = Guid.Parse("10000000-0000-4000-8000-000000000001")
    let attemptId = Guid.Parse("20000000-0000-4000-8000-000000000001")
    let digest = String.replicate 64 "a"
    let timestamp = DateTimeOffset(2026, 9, 9, 10, 11, 12, TimeSpan.Zero)

    let fields =
        {
            IncidentDate = "2026-08-01"
            IncidentNotificationDate = "2026-08-02"
            IncidentCountry = "Latvia"
            ClaimantName = "Synthetic Claimant"
            InsurerName = "Synthetic Insurer"
            ClaimedAmount = "1000.00"
            ClaimedCurrency = "EUR"
            CaseReference = "SYNTHETIC-001"
            PaymentDecisionDate = None
            PayableAmount = None
            PayableCurrency = None
            PaymentDate = None
            Status = CaseStatus.Opened
        }

    let caseView = { Fields = fields; Version = 1L }

    let currentCase =
        {
            Record = caseView
            AvailableCommands = [ CommandKind.Decide; CommandKind.Close ]
        }

    let fourByteReference = String.replicate 80 "😀"

    let fourByteCurrentCase =
        { currentCase with
            Record =
                { caseView with
                    Fields =
                        { fields with
                            CaseReference = fourByteReference
                        }
                }
        }

    let scalarBoundaryCurrentCase =
        let boundary =
            { caseView with
                Fields =
                    { fields with
                        IncidentDate = "0001-01-01"
                        IncidentNotificationDate = "9999-12-31"
                        IncidentCountry = String.replicate 100 "😀"
                        ClaimantName = String.replicate 200 "😀"
                        InsurerName = String.replicate 200 "😀"
                        ClaimedAmount = "999999999999999999.9999"
                        ClaimedCurrency = "ZZZ"
                        CaseReference = fourByteReference
                    }
                Version = Int64.MaxValue - 1L
            }

        match Claim.restore boundary with
        | Ok _ -> { currentCase with Record = boundary }
        | Error _ -> invalidOp "The scalar-boundary corpus fixture must be accepted by Domain."

    let receipt =
        {
            OperationId = operationId
            Snapshot = caseView
            RecordedAt = timestamp
            RecordedBy = "synthetic-operator"
            Replayed = false
            Command = CommandKind.Open
        }

    let observedReceipt = { receipt with Replayed = true }

    let caseSummary =
        {
            CaseReference = fields.CaseReference
            Revision = caseView.Version
            Status = fields.Status
        }

    let change =
        {
            OperationId = operationId
            Revision = caseView.Version
            Command = CommandKind.Open
            RecordedAt = timestamp
            RecordedBy = "synthetic-operator"
        }

    let fault: CoreFault =
        {
            Code = FaultCode.StoreUnavailable
            Message = "Synthetic safe store failure."
            Action = RecommendedAction.RetrySafe
        }

    let rejection: Rejection = Rejection.Domain(DomainError.VersionConflict 2L)

    let recoveryRejection: RecoveryRejection =
        {
            Code = RecoveryRejectionCode.RecoveryActionUnavailable
            Message = "Synthetic recovery refusal."
            Action = RecommendedAction.StopAndInvestigate
        }

    let preparationSummary =
        {
            OperationId = operationId
            CaseReference = fields.CaseReference
            Command = CommandKind.Open
            PreparedAt = timestamp
            State = PreparationState.Unsubmitted
            Authority = RecoveryAuthority.PendingAuthority
            RequestSha256 = Some digest
            AvailableActions =
                [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
        }

    let revokedOperation =
        {
            OperationId = operationId
            RevokedAt = timestamp
            Reason = "Synthetic operator revocation."
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
                    Items =
                        [
                            {
                                AttemptId = attemptId
                                StartedAt = timestamp
                                Settlement = Some "ACCEPTED"
                                SettledAt = Some timestamp
                            }
                        ]
                    NextCursor = None
                    LegacyUncertainty = false
                }
        }

    let review =
        {
            Before = None
            Proposed = caseView
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
                    CaseReference = fields.CaseReference
                    Command = CommandKind.Open
                    ExpectedVersion = 0L
                    AuthoredValues = [ "claimantName", fields.ClaimantName ]
                    CanonicalCommandFormat = 2
                    RequestSha256 = digest
                }
            ExistingPreparation = Some preparationSummary
        }

    let recoveryExport =
        {
            Bytes = [| 1uy |]
            FileName = "claimcore-recovery-synthetic.json"
            MediaType = "application/vnd.claimcore.recovery+json"
            RequestSha256 = digest
        }
