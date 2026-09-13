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
                            Items =
                                [
                                    CliCorpusValues.preparationSummary
                                    WebCorpusSamples.preparationWithoutDigest
                                ]
                            NextCursor = Some "synthetic-recovery-cursor"
                        }
                ))
            sample
                "recovery-list-empty-page"
                endpoint
                (encode (RecoveryQueryOutcome.RecoverySucceeded { Items = []; NextCursor = None }))
        ]
        @ WebCorpusSamples.recoveryFailures "recovery-list" endpoint encode

    let inspect =
        let endpoint = "recovery.inspect"
        let encode = WebWireCodec.recoveryInspect

        let found details observation =
            WebCorpusSamples.recoveryDetails observation details
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
