namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal WebRecoveryQueryCorpusSamples =
    let private sample = WebCorpusSamples.sample

    let list =
        let endpoint = "recovery.list"
        let encode = WebWireCodec.recoveryList

        [
            sample
                "recovery-list-page"
                endpoint
                (encode (
                    RecoveryQueryOutcome.RecoverySucceeded
                        {
                            View = RecoveryListView.Pending
                            Items =
                                [
                                    RetainedRecoveryItem CliCorpusValues.preparationSummary
                                    RetainedRecoveryItem WebCorpusSamples.preparationWithoutDigest
                                ]
                            NextCursor = Some "synthetic-recovery-cursor"
                            PendingPreparationCount = 2
                            PendingCanonicalRequestBytes = 256L
                            MaximumPendingPreparations = 1024
                            MaximumPendingCanonicalRequestBytes = 67108864L
                            NearCapacity = false
                        }
                ))
            sample
                "recovery-list-terminal-revocation"
                endpoint
                (encode (
                    RecoveryQueryOutcome.RecoverySucceeded
                        {
                            View = RecoveryListView.Terminal
                            Items = [ RevokedRecoveryItem CliCorpusValues.revokedOperation ]
                            NextCursor = None
                            PendingPreparationCount = 0
                            PendingCanonicalRequestBytes = 0L
                            MaximumPendingPreparations = 1024
                            MaximumPendingCanonicalRequestBytes = 67108864L
                            NearCapacity = false
                        }
                ))
        ]
        @ WebCorpusSamples.recoveryFailures "recovery-list" endpoint encode

    let inspect =
        let endpoint = "recovery.inspect"
        let encode = WebWireCodec.recoveryInspect

        let found details observation =
            WebCorpusSamples.recoveryDetails observation details
            |> RetainedInspection
            |> Lookup.Found
            |> RecoveryQueryOutcome.RecoverySucceeded
            |> encode

        [
            sample
                "recovery-inspect-found-observed"
                endpoint
                (found CliCorpusValues.preparationDetails (Lookup.Found CliCorpusValues.receipt))
            sample
                "recovery-inspect-found-unobserved"
                endpoint
                (found
                    WebCorpusSamples.detailsWithNullableValues
                    (Lookup.NotFound CliCorpusValues.operationId))
            sample
                "recovery-inspect-found-revoked"
                endpoint
                (encode (
                    RecoveryQueryOutcome.RecoverySucceeded(
                        Lookup.Found(RevokedInspection CliCorpusValues.revokedOperation)
                    )
                ))
            sample
                "recovery-inspect-not-found"
                endpoint
                (encode (
                    RecoveryQueryOutcome.RecoverySucceeded(
                        Lookup.NotFound CliCorpusValues.operationId
                    )
                ))
        ]
        @ WebCorpusSamples.recoveryFailures "recovery-inspect" endpoint encode

    let export =
        let endpoint = "recovery.export"
        let encode = WebWireCodec.recoveryExport

        [
            sample
                "recovery-export-not-found"
                endpoint
                (encode (
                    RecoveryQueryOutcome.RecoverySucceeded(
                        Lookup.NotFound CliCorpusValues.operationId
                    )
                ))
        ]
        @ WebCorpusSamples.recoveryFailures "recovery-export" endpoint encode

    let private preview endpoint success =
        let encode = WebWireCodec.importPreview endpoint

        [
            sample
                (endpoint + "-succeeded")
                endpoint
                (encode (RecoveryQueryOutcome.RecoverySucceeded success))
        ]
        @ WebCorpusSamples.recoveryFailures endpoint endpoint encode

    let imports =
        preview "recovery.importEnvelopePreview" CliCorpusValues.importPreview
        @ preview "recovery.importRecordPreview" WebCorpusSamples.importPreviewWithoutExisting

    let all = list @ inspect @ export @ imports
