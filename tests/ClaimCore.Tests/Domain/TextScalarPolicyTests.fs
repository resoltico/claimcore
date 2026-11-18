module ClaimCore.Tests.TextScalarPolicyTests

open System
open Expecto
open ClaimCore.Domain

let private refusedScalars =
    [
        yield! [ 0..31 ]
        yield! [ 127..159 ]
        yield 0x00AD
        yield 0x061C
        yield! [ 0x115F..0x1160 ]
        yield! [ 0x17B4..0x17B5 ]
        yield 0x200B
        yield! [ 0x200E..0x200F ]
        yield! [ 0x2028..0x202E ]
        yield! [ 0x2060..0x2064 ]
        yield! [ 0x2066..0x2069 ]
        yield 0x3164
        yield 0xFEFF
        yield 0xFFA0
        yield! [ 0xFFF9..0xFFFB ]
        yield! [ 0xE0000..0xE007F ]
        yield! [ 0xFDD0..0xFDEF ]
        for plane in 0..16 do
            yield (plane <<< 16) + 0xFFFE
            yield (plane <<< 16) + 0xFFFF
    ]

let private checkText expected scalar =
    let value = "A" + Char.ConvertFromUtf32 scalar + "B"

    Expect.equal
        (PartyName.parse value |> Result.bind PartyName.admit |> Result.isOk)
        expected
        "Parties retain their scalar policy"

    Expect.equal
        (Reason.parse value |> Result.bind Reason.admit |> Result.isOk)
        expected
        "Reasons retain their scalar policy"

let private unicode =
    testCase
        "every explicitly refused scalar and every deliberately allowed selector retains its meaning"
        (fun () ->
            for scalar in refusedScalars do
                checkText false scalar

            let allowed =
                [
                    0x034F
                    0x180B
                    0x180C
                    0x180D
                    0x180E
                    0x180F
                    0x200C
                    0x200D
                    0xE000
                    0xF0000
                    0x100000
                ]

            for scalar in allowed @ [ 0xFE00..0xFE0F ] @ [ 0xE0100..0xE01EF ] do
                checkText true scalar)

[<Tests>]
let tests = unicode

[<Tests>]
let identifiers =
    testCase
        "ASCII references and supported country codes retain their literal boundaries"
        (fun () ->
            for reference in [ "!"; "~"; String.replicate 80 "a"; "<a>&'" ] do
                Expect.isOk (CaseReference.parse reference) "Printable ASCII reference"

            for reference in [ ""; "a b"; "Rīga"; String.replicate 81 "a"; "a\n" ] do
                Expect.isError (CaseReference.parse reference) "Invalid reference"

            Expect.equal (CaseReference.key "i-Demo") "I-DEMO" "ASCII identity folding"

            for country in [ "LV"; "XK" ] do
                Expect.isOk (CountryCode.parse country) "Supported country"

            for country in [ "ZZ"; "QQ"; "SU"; "Latvia"; "lv"; "L"; "LV\n"; "ĀĀ" ] do
                Expect.isError (CountryCode.parse country) "Invalid or unsupported country")
