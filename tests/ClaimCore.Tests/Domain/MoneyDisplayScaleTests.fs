module ClaimCore.Tests.MoneyDisplayScaleTests

open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "core money display scale"
        [
            testCase
                "ISO data has the pinned publication shape and leaves unknown currencies admissible"
                (fun () ->
                    Expect.equal
                        CurrencyMinorUnits.all.Count
                        165
                        "SIX 2026-09-17 has 165 distinct codes with numeric units"

                    Expect.equal
                        (CurrencyMinorUnits.all |> Map.values |> Seq.countBy id |> Map.ofSeq)
                        (Map.ofList [ 0, 17; 2, 139; 3, 7; 4, 2 ])
                        "The independent source distribution protects the vendored set"

                    for code in CurrencyMinorUnits.all |> Map.keys do
                        Expect.isOk
                            (Currency.parse code)
                            "Every vendored key is a canonical currency code"

                    for code in [ "ZZZ"; "XAU"; "XXX" ] do
                        Expect.isNone
                            (Map.tryFind code CurrencyMinorUnits.all)
                            "Unknown and N.A. values prescribe no minimum"

                        Expect.isOk
                            (Currency.parse code)
                            "Presentation data does not narrow admission")
            testCase
                "meaningful canonical precision wins over ISO minimum without retaining entered zeros"
                (fun () ->
                    for amount, currency, expected in
                        [
                            "100.0000", "EUR", 2
                            "1234.5678", "EUR", 4
                            "150000.0000", "JPY", 0
                            "1.2000", "JPY", 1
                            "0.01", "USD", 2
                            "1", "BHD", 3
                            "1", "CLF", 4
                            "100.0000", "ZZZ", 0
                            "1.2300", "ZZZ", 2
                            "1", "XAU", 0
                            "1.2345", "XAU", 4
                        ] do
                        Expect.equal
                            (Money.displayScale (Inputs.money amount currency))
                            expected
                            "ISO scale never rounds away accepted meaningful digits"

                    for code in [ "EUR"; "JPY"; "ZZZ" ] do
                        Expect.equal
                            (Money.displayScale (Inputs.money "100.0000" code))
                            (Money.displayScale (Inputs.money "100" code))
                            "Trailing-zero input is an independent negative witness against stored decimal scale")
        ]
