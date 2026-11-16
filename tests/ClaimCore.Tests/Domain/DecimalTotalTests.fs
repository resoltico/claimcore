module ClaimCore.Tests.DecimalTotalTests

open Expecto
open ClaimCore.Domain

let private accepted result =
    result |> Result.defaultWith (fun value -> failtestf "%A" value)

[<Tests>]
let tests =
    testList
        "exact decimal totals"
        [
            testCase "sums exceed single-amount precision without rounding" (fun () ->
                let maximum = DecimalTotal.parse "999999999999999999.9999" |> accepted

                Expect.equal
                    (DecimalTotal.sum [ maximum; maximum ] |> Result.map DecimalTotal.format)
                    (Ok "1999999999999999999.9998")
                    "Independent exact carry across the integer boundary"

                Expect.equal
                    (DecimalTotal.sum [ maximum; DecimalTotal.parse "0.0001" |> accepted ]
                     |> Result.map DecimalTotal.format)
                    (Ok "1000000000000000000")
                    "Fractional carry remains exact")
            testCase "the 40 integer and four fractional digit bounds are enforced" (fun () ->
                let raw = String.replicate 40 "9" + ".9999"
                let maximum = DecimalTotal.parse raw |> accepted
                Expect.equal (DecimalTotal.format maximum) raw "Largest admitted total"

                Expect.isError
                    (DecimalTotal.sum [ maximum; DecimalTotal.parse "0.0001" |> accepted ])
                    "Overflow never rounds"

                for invalid in
                    [ "-1"; "+1"; "01"; "1e2"; ".1"; "1."; "1.00001"; String.replicate 41 "1" ] do
                    Expect.isError (DecimalTotal.parse invalid) "Closed unsigned exact grammar")
        ]
