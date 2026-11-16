module ClaimCore.Tests.DecimalTotalCodecTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Json

[<Tests>]
let tests =
    testList
        "exact total wire representation"
        [
            testCase "wire totals require canonical decimal spelling" (fun () ->
                for raw in [ "1.0"; "0.0000"; "01"; "-1" ] do
                    Expect.isError
                        (CaseTotalsCodecs.total.Read "$" (JsonValue.String raw))
                        "No alternate wire spelling"

                Expect.isOk
                    (CaseTotalsCodecs.total.Read
                        "$"
                        (JsonValue.String "123456789012345678901234567890.1234"))
                    "The wire scalar reaches beyond System.Decimal")
        ]
