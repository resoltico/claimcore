namespace ClaimCore.ContractGeneration

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal WebQueryCorpusSamples =
    let private sample = WebCorpusSamples.sample

    let foundation =
        [
            sample
                "session-anonymous-null-token"
                "session"
                (WebWireCodec.session "session" false None)
            sample
                "session-authenticated-token"
                "session"
                (WebWireCodec.session "session" true (Some "synthetic-token"))
            sample
                "session-login"
                "session.login"
                (WebWireCodec.session "session.login" true (Some "synthetic-token"))
            sample
                "session-logout"
                "session.logout"
                (WebWireCodec.session "session.logout" false (Some "synthetic-token"))
            sample
                "definition-described"
                "definition"
                (WebWireCodec.description WebCorpusSamples.description)
        ]

    let caseGet =
        let endpoint = "case.get"
        let encode = WebWireCodec.get

        [
            sample
                "case-get-found"
                endpoint
                (encode (QueryOutcome.Succeeded(Lookup.Found CliCorpusValues.currentCase)))
            sample
                "case-get-found-four-byte-boundary"
                endpoint
                (encode (QueryOutcome.Succeeded(Lookup.Found CliCorpusValues.fourByteCurrentCase)))
            sample
                "case-get-found-scalar-boundaries"
                endpoint
                (encode (
                    QueryOutcome.Succeeded(Lookup.Found CliCorpusValues.scalarBoundaryCurrentCase)
                ))
            sample
                "case-get-found-optionals"
                endpoint
                (encode (
                    QueryOutcome.Succeeded(
                        Lookup.Found
                            {
                                Record = WebCorpusSamples.caseViewWithOptionals
                                AvailableCommands = []
                            }
                    )
                ))
            sample
                "case-get-not-found"
                endpoint
                (encode (
                    QueryOutcome.Succeeded(Lookup.NotFound CliCorpusValues.fields.CaseReference)
                ))
        ]
        @ WebCorpusSamples.queryFailures "case-get" endpoint encode CliCorpusValues.rejection

    let caseList =
        let endpoint = "case.list"
        let encode = WebWireCodec.list

        [
            sample
                "case-list-page"
                endpoint
                (encode (
                    QueryOutcome.Succeeded
                        {
                            Items = [ CliCorpusValues.caseSummary ]
                            NextAfterReference = Some CliCorpusValues.fields.CaseReference
                        }
                ))
            sample
                "case-list-empty-page"
                endpoint
                (encode (
                    QueryOutcome.Succeeded
                        {
                            Items = []
                            NextAfterReference = None
                        }
                ))
        ]
        @ WebCorpusSamples.queryFailures
            "case-list"
            endpoint
            encode
            WebCorpusSamples.rejectionWithField

    let caseHistory =
        let endpoint = "case.history"
        let encode = WebWireCodec.history

        [
            sample
                "case-history-found"
                endpoint
                (encode (
                    QueryOutcome.Succeeded(
                        Lookup.Found
                            {
                                Entries =
                                    [
                                        HistoryEntry.SummaryEntry CliCorpusValues.change
                                        HistoryEntry.FullEntry WebCorpusSamples.replayedReceipt
                                    ]
                                NextCursor = Some "synthetic-history-cursor"
                            }
                    )
                ))
            sample
                "case-history-found-empty"
                endpoint
                (encode (QueryOutcome.Succeeded(Lookup.Found { Entries = []; NextCursor = None })))
            sample
                "case-history-not-found"
                endpoint
                (encode (
                    QueryOutcome.Succeeded(Lookup.NotFound CliCorpusValues.fields.CaseReference)
                ))
        ]
        @ WebCorpusSamples.queryFailures "case-history" endpoint encode CliCorpusValues.rejection

    let operation =
        let endpoint = "operation.observe"
        let encode = WebWireCodec.observe

        [
            sample
                "operation-observe-found"
                endpoint
                (encode (QueryOutcome.Succeeded(Lookup.Found WebCorpusSamples.replayedReceipt)))
            sample
                "operation-observe-not-found"
                endpoint
                (encode (QueryOutcome.Succeeded(Lookup.NotFound CliCorpusValues.operationId)))
        ]
        @ WebCorpusSamples.queryFailures
            "operation-observe"
            endpoint
            encode
            WebCorpusSamples.rejectionWithField

    let all = foundation @ caseGet @ caseList @ caseHistory @ operation
