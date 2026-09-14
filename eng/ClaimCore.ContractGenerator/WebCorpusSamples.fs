namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal WebEncodedSample =
    {
        Identifier: string
        Endpoint: string
        Bytes: byte array
    }

[<RequireQualifiedAccess>]
module internal WebCorpusSamples =
    let sample identifier endpoint bytes =
        {
            Identifier = identifier
            Endpoint = endpoint
            Bytes = bytes
        }

    let alphaOperationId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

    let description =
        {
            Contract = SemanticContract.current
            SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
            Runtime =
                {
                    ProductVersion = "0.1.0"
                    EffectiveBusinessDate = DateOnly(2026, 9, 9)
                    TimeZoneId = "Etc/UTC"
                }
        }

    let rejectionWithField: Rejection =
        { CliCorpusValues.rejection with
            Field = Some "claimedAmount"
            ActualVersion = None
        }

    let caseViewWithOptionals =
        {
            Fields =
                { CliCorpusValues.fields with
                    PaymentDecisionDate = Some "2026-08-10"
                    PayableAmount = Some "750.00"
                    PayableCurrency = Some "EUR"
                    PaymentDate = Some "2026-08-11"
                    Status = CaseStatus.Closed
                }
            Version = 2L
        }

    let replayedReceipt =
        { CliCorpusValues.receipt with
            Snapshot = caseViewWithOptionals
            Replayed = true
            Command = CommandKind.Close
        }

    let preparationWithoutDigest =
        { CliCorpusValues.preparationSummary with
            State = PreparationState.Dismissed
            Authority = RecoveryAuthority.RevokedAuthority
            RequestSha256 = None
            AvailableActions = []
        }

    let detailsWithNullableValues =
        let attempt (attemptId: string) settlement settledAt =
            {
                AttemptId = Guid.Parse(attemptId)
                StartedAt = CliCorpusValues.timestamp
                Settlement = settlement
                SettledAt = settledAt
            }

        { CliCorpusValues.preparationDetails with
            Summary = preparationWithoutDigest
            AuthoredValues = []
            PreparingContractKind = "LEGACY_UNCLASSIFIED"
            Attempts =
                {
                    Items =
                        [
                            attempt "30000000-0000-4000-8000-000000000001" None None
                            attempt
                                "30000000-0000-4000-8000-000000000002"
                                (Some "REJECTED")
                                (Some CliCorpusValues.timestamp)
                            attempt
                                "30000000-0000-4000-8000-000000000003"
                                (Some "ERROR")
                                (Some CliCorpusValues.timestamp)
                        ]
                    NextCursor = Some "synthetic-attempt-cursor"
                    LegacyUncertainty = true
                }
        }

    let reviewWithNullableValues =
        { CliCorpusValues.review with
            Before = Some CliCorpusValues.caseView
            Proposed = caseViewWithOptionals
            Changes =
                [
                    {
                        FieldName = "claimedAmount"
                        Before = Some CliCorpusValues.fields.ClaimedAmount
                        After = None
                    }
                    {
                        FieldName = "paymentDate"
                        Before = None
                        After = Some "2026-08-11"
                    }
                ]
        }

    let importPreviewWithoutExisting =
        { CliCorpusValues.importPreview with
            ArtifactKind = RecoveryArtifactKind.UnboundCanonicalRecord
            ExistingPreparation = None
        }

    let recoveryDetails observation details =
        {
            Preparation = details
            Observation = observation
        }

    let queryFailures prefix endpoint (encode: QueryOutcome<'value> -> byte array) rejection =
        [
            sample (prefix + "-rejected") endpoint (encode (QueryOutcome.Rejected rejection))
            sample
                (prefix + "-failed")
                endpoint
                (encode (QueryOutcome.Failed CliCorpusValues.fault))
            sample (prefix + "-cancelled") endpoint (encode QueryOutcome.Cancelled)
        ]

    let recoveryFailures prefix endpoint (encode: RecoveryQueryOutcome<'value> -> byte array) =
        [
            sample
                (prefix + "-rejected")
                endpoint
                (encode (RecoveryQueryOutcome.RecoveryRejected CliCorpusValues.recoveryRejection))
            sample
                (prefix + "-failed")
                endpoint
                (encode (RecoveryQueryOutcome.RecoveryFailed CliCorpusValues.fault))
            sample (prefix + "-cancelled") endpoint (encode RecoveryQueryOutcome.RecoveryCancelled)
        ]
