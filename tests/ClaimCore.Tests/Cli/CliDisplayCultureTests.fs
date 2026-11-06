module ClaimCore.Tests.CliDisplayCultureTests

open System
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "CLI regional display cultures"
        [
            testCase "prepared display cultures are shared and immutable" (fun () ->
                for language in ClaimCore.Protocol.LocalizationPolicy.languages do
                    let culture = TextReplyFormat.displayCulture language
                    Expect.isTrue culture.IsReadOnly "Culture is not mutable shared state"

                    Expect.isTrue
                        culture.DateTimeFormat.IsReadOnly
                        "Date conventions cannot change between calls"

                    Expect.isTrue
                        culture.NumberFormat.IsReadOnly
                        "Money conventions cannot change between calls"

                    Expect.isTrue
                        (obj.ReferenceEquals(culture, TextReplyFormat.displayCulture language))
                        "Same finite language resource is reused"

                    Expect.throws
                        (fun () -> culture.NumberFormat.NumberDecimalSeparator <- "!")
                        "Callers cannot mutate exact money conventions")
            testCase
                "supported languages select explicit independent regional identities"
                (fun () ->
                    for language, expected in [ "en", "en-GB"; "lv", "lv-LV"; "ar", "ar-AE" ] do
                        Expect.equal
                            (TextReplyFormat.displayCulture language).Name
                            expected
                            "Neutral Arabic differs across Mac and Linux; the Latin-digit Arabic choice is explicit")
            testCase
                "grouping decimal symbols and all scales have independent literal goldens"
                (fun () ->
                    for language, expected in
                        [
                            "en",
                            [
                                "1,234.50 EUR"
                                "1,234.5000 EUR"
                                "1,234 JPY"
                                "1.2 JPY"
                                "1.000 BHD"
                                "0.0000 EUR"
                                "1,234.5678 EUR"
                                "1,234.5678 ZZZ"
                            ]
                            "lv",
                            [
                                "1\u00a0234,50 EUR"
                                "1\u00a0234,5000 EUR"
                                "1\u00a0234 JPY"
                                "1,2 JPY"
                                "1,000 BHD"
                                "0,0000 EUR"
                                "1\u00a0234,5678 EUR"
                                "1\u00a0234,5678 ZZZ"
                            ]
                            "ar",
                            [
                                "1,234.50 EUR"
                                "1,234.5000 EUR"
                                "1,234 JPY"
                                "1.2 JPY"
                                "1.000 BHD"
                                "0.0000 EUR"
                                "1,234.5678 EUR"
                                "1,234.5678 ZZZ"
                            ]
                        ] do
                        let actual =
                            [
                                TextReplyFormat.money language (Inputs.money "1234.5" "EUR")
                                TextReplyFormat.moneyWithScale
                                    language
                                    (Inputs.money "1234.5" "EUR")
                                    4
                                TextReplyFormat.money language (Inputs.money "1234" "JPY")
                                TextReplyFormat.money language (Inputs.money "1.2" "JPY")
                                TextReplyFormat.money language (Inputs.money "1" "BHD")
                                TextReplyFormat.moneyWithScale language (Inputs.money "0" "EUR") 4
                                TextReplyFormat.moneyWithScale
                                    language
                                    (Inputs.money "1234.5678" "EUR")
                                    1
                                TextReplyFormat.money language (Inputs.money "1234.5678" "ZZZ")
                            ]

                        Expect.equal
                            actual
                            expected
                            "Grouping never replaces canonical precision or supplied display scale")
            testCase
                "forty-digit aggregate money stays exact in every CLI display culture"
                (fun () ->
                    let canonical = "1234567890123456789012345678901234567890.1234"
                    let total = DecimalTotal.parse canonical |> Fixtures.accepted
                    let currency = Currency.parse "EUR" |> Fixtures.accepted

                    for language, expected in
                        [
                            "en", "1,234,567,890,123,456,789,012,345,678,901,234,567,890.1234 EUR"
                            "lv",
                            "1\u00a0234\u00a0567\u00a0890\u00a0123\u00a0456\u00a0789\u00a0012\u00a0345\u00a0678\u00a0901\u00a0234\u00a0567\u00a0890,1234 EUR"
                            "ar", "1,234,567,890,123,456,789,012,345,678,901,234,567,890.1234 EUR"
                        ] do
                        Expect.equal
                            (TextReplyFormat.moneyTotalWithScale language currency total 4)
                            expected
                            "No conversion to Amount or floating point"

                    Expect.equal
                        (DecimalTotal.format total)
                        canonical
                        "Human grouping never changes the canonical aggregate")
            testCase
                "dates and observation footers share the declared Gregorian display culture"
                (fun () ->
                    let basis: WorkDateBasis =
                        {
                            ObservedAt = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
                            BusinessDate = DateOnly(2026, 11, 4)
                            TimeZoneId = "Etc/UTC"
                        }

                    for language, date, footer in
                        [
                            "en",
                            "4 Nov 2026",
                            "Observed 2026-11-04 12:00 · business date 4 Nov 2026 (Etc/UTC)"
                            "lv",
                            "4 nov. 2026",
                            "Novērots 2026-11-04 12:00 · darba datums 4 nov. 2026 (Etc/UTC)"
                            "ar",
                            "4 نوفمبر 2026",
                            "رُصد في 2026-11-04 12:00 · تاريخ العمل 4 نوفمبر 2026 (Etc/UTC)"
                        ] do
                        Expect.equal
                            (TextReplyFormat.date language (Inputs.date "2026-11-04"))
                            date
                            "The business date is preserved"

                        Expect.equal
                            (TextObservation.line language basis)
                            footer
                            "Only the supplied snapshot provides the observation")
        ]
