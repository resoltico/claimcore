module ClaimCore.Tests.ScalarPropertyTests

open System
open System.Globalization
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.PropertyHarness

type private Sample = { Raw: string; Accepted: bool }

let private restoreWith transform =
    let fields = opened () |> Claim.view |> (fun view -> transform view.Fields)
    Claim.restore { Fields = fields; Version = 1L }

let private validDate =
    Gen.int32 (Range.constant DateOnly.MinValue.DayNumber DateOnly.MaxValue.DayNumber)
    |> Gen.map (fun day ->
        {
            Raw = DateOnly.FromDayNumber(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            Accepted = true
        })

let private invalidDate =
    [
        "0000-01-01"
        "10000-01-01"
        "2024-02-30"
        "2023-02-29"
        "2024-2-01"
        "2024-01-1"
        "2024-01-01 "
        "+2024-01-01"
        "２０２４-01-01"
    ]
    |> Gen.item
    |> Gen.map (fun raw -> { Raw = raw; Accepted = false })

let private dateProperty =
    property {
        let! sample = Gen.frequency [ 3, validDate; 2, invalidDate ]

        let result =
            restoreWith (fun fields ->
                { fields with
                    IncidentDate = sample.Raw
                    IncidentNotificationDate = sample.Raw
                })

        return
            match result, sample.Accepted with
            | Ok claim, true ->
                let fields = (Claim.view claim).Fields
                fields.IncidentDate = sample.Raw && fields.IncidentNotificationDate = sample.Raw
            | Error _, false -> true
            | _ -> false
    }

let private powerOfTen scale =
    [ 1..scale ] |> List.fold (fun value _ -> value * 10u) 1u

let private formattedAmount (whole: uint64) (scale: int) (fraction: uint32) =
    if scale = 0 then
        whole.ToString(CultureInfo.InvariantCulture)
    else
        let decimals = fraction.ToString("D" + string scale, CultureInfo.InvariantCulture)
        whole.ToString(CultureInfo.InvariantCulture) + "." + decimals

let private variedValidAmount =
    gen {
        let! whole = Gen.uint64 (Range.constant 0UL 999_999_999_999_999_999UL)
        let! scale = Gen.int32 (Range.constant 0 4)
        let! fraction = Gen.uint32 (Range.constant 0u (powerOfTen scale - 1u))
        return formattedAmount whole scale fraction
    }

let private validAmount =
    Gen.frequency
        [
            1, Gen.constant "999999999999999999.9999"
            1, Gen.constant "0"
            8, variedValidAmount
        ]
    |> Gen.map (fun raw -> { Raw = raw; Accepted = true })

let private invalidAmount =
    [
        "-1"
        "+1"
        "01"
        "1."
        ".1"
        "1.00000"
        "1000000000000000000"
        "1e2"
        "NaN"
        " 1"
    ]
    |> Gen.item
    |> Gen.map (fun raw -> { Raw = raw; Accepted = false })

let private amountProperty =
    property {
        let! sample = Gen.frequency [ 3, validAmount; 2, invalidAmount ]

        let result =
            restoreWith (fun fields ->
                { fields with
                    ClaimedAmount = sample.Raw
                })

        return Result.isOk result = sample.Accepted
    }

let private validCurrency =
    Gen.array (Range.singleton 3) (Gen.char 'A' 'Z')
    |> Gen.map (fun characters ->
        {
            Raw = String(characters)
            Accepted = true
        })

let private invalidCurrency =
    [ ""; "EU"; "EURO"; "eur"; "E1R"; "ÉUR"; " EUR"; "EU\u0000" ]
    |> Gen.item
    |> Gen.map (fun raw -> { Raw = raw; Accepted = false })

let private currencyProperty =
    property {
        let! sample = Gen.frequency [ 3, validCurrency; 2, invalidCurrency ]

        let result =
            restoreWith (fun fields ->
                { fields with
                    ClaimedCurrency = sample.Raw
                })

        return Result.isOk result = sample.Accepted
    }

type private TextTarget =
    | Reference
    | Country
    | Claimant
    | Insurer

let private textTargets = [ Reference; Country; Claimant; Insurer ]

let private textLimit =
    function
    | Reference -> 80
    | Country -> 100
    | Claimant
    | Insurer -> 200

let private validateText target value =
    let baseRequest = request 0L (Command.Open registration)

    let candidate =
        match target with
        | Reference ->
            { baseRequest with
                CaseReference = value
            }
        | Country ->
            { baseRequest with
                Command =
                    Command.Open
                        { registration with
                            IncidentCountry = value
                        }
            }
        | Claimant ->
            { baseRequest with
                Command =
                    Command.Open
                        { registration with
                            ClaimantName = value
                        }
            }
        | Insurer ->
            { baseRequest with
                Command =
                    Command.Open
                        { registration with
                            InsurerName = value
                        }
            }

    Claim.validateRequest candidate

let private validText =
    gen {
        let! target = Gen.item textTargets
        let! length = Gen.int32 (Range.constant 1 (textLimit target))
        let! supplementary = Gen.bool
        let value = String.replicate length (if supplementary then "😀" else "x")
        return target, { Raw = value; Accepted = true }
    }

let private invalidText =
    gen {
        let! target = Gen.item textTargets

        let! value =
            Gen.item
                [
                    ""
                    " "
                    " leading"
                    "trailing "
                    "control\u0000value"
                    String([| char 0xD800 |])
                    String.replicate (textLimit target + 1) "x"
                ]

        return target, { Raw = value; Accepted = false }
    }

let private textProperty =
    property {
        let! target, sample = Gen.frequency [ 3, validText; 2, invalidText ]
        return Result.isOk (validateText target sample.Raw) = sample.Accepted
    }

let tests =
    testList
        "Hedgehog scalar boundaries"
        [
            testCase "Gregorian date grammar and range" (fun () ->
                run "CC-PROP-DATE-001" dateProperty)
            testCase "amount precision and magnitude" (fun () ->
                run "CC-PROP-AMOUNT-001" amountProperty)
            testCase "uppercase ASCII currency grammar" (fun () ->
                run "CC-PROP-CURRENCY-001" currencyProperty)
            testCase "Unicode text and length boundaries" (fun () ->
                let invalidValues =
                    [
                        "\u00a0"
                        "\u00a0value"
                        "value\u3000"
                        "prefix\u0000suffix"
                        "prefix\u0080suffix"
                        String([| char 0xD800 |])
                        String([| char 0xDFFF |])
                        String.replicate 81 "😀"
                    ]

                Expect.isOk
                    (validateText Reference (String.replicate 80 "😀"))
                    "Supplementary Unicode length is counted in scalar values"

                invalidValues
                |> List.iter (fun value ->
                    Expect.isError (validateText Reference value) "Exact Domain text boundary")

                run "CC-PROP-TEXT-001" textProperty)
        ]
