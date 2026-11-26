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
            for reference in [ "A"; "cc"; "cC9-1"; "C-9"; String.replicate 32 "a"; "Del-Ta9" ] do
                Expect.isOk (CaseReference.parseCustom reference) "Custom ASCII reference"

            for reference in
                [
                    ""
                    "a b"
                    "Rīga"
                    String.replicate 33 "a"
                    "a\n"
                    "a\r\n"
                    "A--B"
                    "A-"
                    "1A"
                    "A_B"
                    "cc-"
                    "cC-0"
                    "CC-0000-0000-0000-0000-98"
                ] do
                Expect.isError (CaseReference.parseCustom reference) "Invalid custom reference"

            Expect.equal (CaseReference.key "i-Demo") "I-DEMO" "ASCII identity folding"

            for country in [ "LV"; "XK" ] do
                Expect.isOk (CountryCode.parse country) "Supported country"

            for country in [ "ZZ"; "QQ"; "SU"; "Latvia"; "lv"; "L"; "LV\n"; "ĀĀ" ] do
                Expect.isError (CountryCode.parse country) "Invalid or unsupported country")

[<Tests>]
let generatedReferences =
    testList
        "generated case references"
        [
            testCase
                "fixed big-endian vectors retain leading zeroes and canonical spelling"
                (fun () ->
                    for bytes, reference in
                        [
                            "00000000000000000000", "CC-0000-0000-0000-0000-98"
                            "00000000000000000001", "CC-0000-0000-0000-0001-95"
                            "00443214C74254B635CF", "CC-0123-4567-89AB-CDEF-41"
                            "3CC74BA342B70CDEA638", "CC-7K3M-Q8T2-PW6D-X9HR-49"
                            "FFFFFFFFFFFFFFFFFFFF", "CC-ZZZZ-ZZZZ-ZZZZ-ZZZZ-93"
                        ] do
                        let generated = CaseReference.generate (Convert.FromHexString bytes)
                        Expect.equal (CaseReference.format generated) reference "Printed reference"
                        Expect.isTrue (CaseReference.isGenerated generated) "Generated namespace"
                        let exact = reference.ToLowerInvariant()
                        let lookup = CaseReference.parse exact |> Result.toOption |> Option.get

                        Expect.equal
                            (CaseReference.format lookup)
                            exact
                            "Selector retains spelling"

                        Expect.equal (CaseReference.canonical lookup) generated "Folded identity"

                    for length in [ 0; 9; 11 ] do
                        Expect.throws
                            (fun () -> CaseReference.generate (Array.zeroCreate length) |> ignore)
                            "Entropy length must be exactly ten bytes")
            testCase "exact check digits are required independently of lexical shape" (fun () ->
                for value in
                    [
                        "CC-0000-0000-0000-0000-01"
                        "CC-0000-0000-0000-0021-00"
                        "CC-0000-0000-0000-0000-97"
                        "CC-0124-3567-89AB-CDEF-41"
                        "CC-0123-4567-89AB-CDEG-41"
                        "CC-000O-0000-0000-0000-98"
                        "CC-0000-0000-0000-000I-98"
                        "CC-0000-0000-0000-000L-98"
                        "CC-0000-0000-0000-000U-98"
                        "CC-000-0000-0000-0000-98"
                        "CC-00000-0000-0000-0000-98"
                        "CC-0000-00000000-0000-98"
                        "CC-0000-0000-0000-0000-98 "
                        "CC–0000-0000-0000-0000-98"
                    ] do
                    Expect.equal
                        (CaseReference.parse value)
                        (Error Violation.ReferenceFormat)
                        value

                Expect.isOk
                    (CaseReference.parse "CC-0000-0000-0000-0021-97")
                    "The canonical pair for residue 65 is 97")
            testCase
                "codec rejects all substitutions and adjacent unequal swaps in a fixed payload"
                (fun () ->
                    let original = "CC-0123-4567-89AB-CDEF-41"
                    let alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
                    let positions = [ 3; 4; 5; 6; 8; 9; 10; 11; 13; 14; 15; 16; 18; 19; 20; 21 ]

                    let reject (changed: char array) =
                        let reference = String changed

                        Expect.equal
                            (CaseReference.parse reference)
                            (Error Violation.ReferenceFormat)
                            reference

                    for position in positions do
                        for symbol in alphabet do
                            if symbol <> original[position] then
                                let changed = original.ToCharArray()
                                changed[position] <- symbol
                                reject changed

                    for first, second in List.pairwise positions do
                        let changed = original.ToCharArray()
                        changed[first] <- original[second]
                        changed[second] <- original[first]
                        reject changed)
        ]
