module ClaimCore.Tests.ScalarPropertyTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open System.Globalization
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.PropertyHarness

type private Sample = { Raw: string; Accepted: bool }

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

        return
            match CalendarDate.parse sample.Raw, sample.Accepted with
            | Ok date, true -> CalendarDate.format date = sample.Raw
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

        let candidate =
            request
                0L
                (UnvalidatedCaseAct.Open
                    { registration with
                        ClaimedAmount = sample.Raw
                    })

        let result = CaseFixtures.decide today candidate None

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
        return Result.isOk (Money.parse "1" sample.Raw) = sample.Accepted
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
    | Country -> 2
    | Claimant
    | Insurer -> 200

let private validateText target value =
    let facts =
        match target with
        | Country ->
            { registration with
                IncidentCountry = value
            }
        | Claimant ->
            { registration with
                ClaimantName = value
            }
        | Insurer ->
            { registration with
                InsurerName = value
            }
        | Reference -> registration

    let candidate = request 0L (UnvalidatedCaseAct.Open facts)

    let candidate =
        if target = Reference then
            { candidate with CaseReference = value }
        else
            candidate

    CaseFixtures.decide today candidate None |> Result.map ignore

let private validText =
    gen {
        let! target = Gen.item textTargets
        let! length = Gen.int32 (Range.constant 1 (textLimit target))
        let! supplementary = Gen.bool

        let value =
            match target with
            | Country -> "LV"
            | Reference -> String.replicate length "x"
            | _ -> String.replicate length (if supplementary then "😀" else "x")

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

[<Tests>]
let tests =
    testList
        "Hedgehog scalar boundaries"
        [
            testCase "Gregorian date grammar and range" (fun () -> run "calendar date" dateProperty)
            testCase "amount precision and magnitude" (fun () -> run "money amount" amountProperty)
            testCase "uppercase ASCII currency grammar" (fun () ->
                run "currency code" currencyProperty)
            testCase "Unicode text and length boundaries" (fun () ->
                let invalidValues =
                    [
                        " "
                        " value"
                        "value　"
                        "prefix\u0000suffix"
                        "prefixsuffix"
                        String([| char 0xD800 |])
                        String([| char 0xDFFF |])
                        String.replicate 201 "😀"
                    ]

                Expect.isOk
                    (validateText Claimant (String.replicate 200 "😀"))
                    "Supplementary Unicode length is counted in scalar values"

                invalidValues
                |> List.iter (fun value ->
                    Expect.isError (validateText Claimant value) "Exact Domain text boundary")

                run "business text" textProperty)
        ]
