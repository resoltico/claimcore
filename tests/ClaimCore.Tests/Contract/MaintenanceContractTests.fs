module ClaimCore.Tests.MaintenanceContractTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation

[<Tests>]
let tests =
    testCase "maintenance advice distinguishes valid shape from truthful availability" (fun () ->
        let read text =
            Codec.decode
                Limits.MaximumResponseBytes
                ResponseCodecs.caseAdvice
                (Encoding.UTF8.GetBytes(Fixtures.completeAdviceWire text))

        let valid =
            read
                """{"acts":[],"history":"FULL","recommended":null,"maintenance":{"active":true}}"""

        match valid with
        | Ok advice ->
            Expect.isEmpty advice.Acts "Paused advice offers no new register writes"

            Expect.equal
                advice.History
                HistoryAccess.Full
                "Maintenance does not invent a history restriction"

            Expect.equal advice.Maintenance.Active true "The observed operational state survives"
        | Error problem -> failtestf "Independent paused advice must decode: %A" problem

        Expect.isError
            (read
                """{"acts":["case.close"],"history":"FULL","recommended":null,"maintenance":{"active":true}}""")
            "A structurally valid write offer contradicts maintenance and is rejected semantically"

        Expect.isError
            (read
                """{"acts":[],"history":"NONE","recommended":null,"maintenance":{"active":true,"generation":0}}""")
            "Retired generation metadata is not accepted")
