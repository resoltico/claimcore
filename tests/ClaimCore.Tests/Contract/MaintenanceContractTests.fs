module ClaimCore.Tests.MaintenanceContractTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Protocol

[<Tests>]
let tests =
    testCase "maintenance advice distinguishes valid shape from truthful availability" (fun () ->
        let read text =
            Codec.decode
                Limits.MaximumResponseBytes
                ResponseCodecs.caseAdvice
                (Encoding.UTF8.GetBytes(text: string))

        let valid =
            read """{"acts":[],"history":["FULL"],"maintenance":{"active":true,"generation":3}}"""

        match valid with
        | Ok advice ->
            Expect.isEmpty advice.Acts "Paused advice offers no new register writes"

            Expect.equal
                advice.History
                [ HistoryDetail.Full ]
                "Maintenance does not invent a history restriction"

            Expect.equal
                (advice.Maintenance.Active, advice.Maintenance.Generation)
                (true, 3L)
                "The observed operational state survives"
        | Error problem -> failtestf "Independent paused advice must decode: %A" problem

        Expect.isError
            (read
                """{"acts":["CLOSE"],"history":["FULL"],"maintenance":{"active":true,"generation":3}}""")
            "A structurally valid write offer contradicts maintenance and is rejected semantically"

        Expect.isError
            (read """{"acts":[],"history":[],"maintenance":{"active":true,"generation":0}}""")
            "An active interval requires a positive generation")
