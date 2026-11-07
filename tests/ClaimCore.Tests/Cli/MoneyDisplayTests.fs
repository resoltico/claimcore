module ClaimCore.Tests.MoneyDisplayTests

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "human money goldens"
        [
            testCase "English grouping and ISO minimum retain every meaningful digit" (fun () ->
                for amount, currency, expected in
                    [
                        "100", "EUR", "100.00 EUR"
                        "100.0000", "EUR", "100.00 EUR"
                        "150000", "JPY", "150,000 JPY"
                        "1234.5678", "EUR", "1,234.5678 EUR"
                        "0.01", "USD", "0.01 USD"
                        "100.0000", "ZZZ", "100 ZZZ"
                        "1234.5000", "ZZZ", "1,234.5 ZZZ"
                        "100.0000", "XAU", "100 XAU"
                        "1", "BHD", "1.000 BHD"
                        "1", "CLF", "1.0000 CLF"
                    ] do
                    Expect.equal
                        (TextReplyFormat.money "en" (Inputs.money amount currency))
                        expected
                        "Independent literal human-output golden")
            testCase
                "all supported locales preserve exact decimal values at admission boundaries"
                (fun () ->
                    for language, displayLocale in [ "en", "en-GB"; "lv", "lv-LV"; "ar", "ar-AE" ] do
                        for amount in [ "0"; "0.0001"; "10.0100"; "999999999999999999.9999" ] do
                            for currency in [ "EUR"; "JPY"; "BHD"; "CLF"; "ZZZ" ] do
                                let value = Inputs.money amount currency
                                let rendered = TextReplyFormat.money language value
                                let number = rendered.Substring(0, rendered.Length - 4)

                                Expect.equal
                                    (Decimal.Parse(
                                        number,
                                        NumberStyles.Number,
                                        CultureInfo.GetCultureInfo displayLocale
                                    ))
                                    (Money.value value)
                                    "Human grouping and padding never pass through floating point or round the value")
            testCase "canonical Money and JSON representations are unchanged" (fun () ->
                let value = Inputs.money "100.0000" "EUR"

                Expect.equal
                    (Money.format value)
                    "100"
                    "Canonical values do not acquire presentation padding"

                Expect.equal
                    (Codec.encode ResponseScalars.money value |> Encoding.UTF8.GetString)
                    "{\"amount\":\"100\",\"currency\":\"EUR\"}"
                    "Structured replies stay exact canonical data")
            testCase "catalog exposes the same pinned data with numeric minor units" (fun () ->
                let values =
                    Catalog.document.MoneyMinorUnits
                    |> List.map (fun entry -> entry.Currency, entry.MinorUnits)
                    |> Map.ofList

                Expect.equal
                    values
                    CurrencyMinorUnits.all
                    "The catalog is a projection of the Domain owner"

                for code, expected in [ "JPY", 0; "EUR", 2; "BHD", 3; "CLF", 4 ] do
                    Expect.equal
                        (Map.find code values)
                        expected
                        "Independent ISO examples cover every scale"

                Expect.isOk
                    (CatalogCodecs.codec.Write Catalog.document |> CatalogCodecs.codec.Read "$")
                    "The new catalog field has matching read/write schema")
        ]
