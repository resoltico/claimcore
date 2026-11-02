module ClaimCore.Tests.NarrativeValueTests

open System
open Expecto
open ClaimCore.Domain

let private description value =
    IncidentDescription.parse value
    |> Result.bind IncidentDescription.admit
    |> Result.map IncidentDescription.format

let private summary value =
    HandlingSummary.parse value
    |> Result.bind HandlingSummary.admit
    |> Result.map HandlingSummary.format

let private action value =
    ReminderAction.parse value
    |> Result.bind ReminderAction.admit
    |> Result.map ReminderAction.format

[<Tests>]
let tests =
    testList
        "narrative values"
        [
            testCase "scalar boundaries are independent of UTF16 and visible glyph count" (fun () ->
                for admit in [ description; summary ] do
                    for scalar in [ "a"; "😀"; "\u0301" ] do
                        for count in [ 599; 600 ] do
                            let value = String.replicate count scalar
                            Expect.equal (admit value) (Ok value) "At/below limit is exact"

                        Expect.equal
                            (admit (String.replicate 601 scalar))
                            (Error(Violation.TooLong 600))
                            "A 601st scalar is refused"

                    let combining = String.replicate 300 "e\u0301"

                    Expect.equal
                        (admit combining)
                        (Ok combining)
                        "Combining marks count separately"

                    Expect.equal
                        (admit (combining + "e"))
                        (Error(Violation.TooLong 600))
                        "Not glyphs")
            testCase "paragraphs Arabic joiners and authored whitespace are preserved" (fun () ->
                for admit in [ description; summary ] do
                    for value in
                        [
                            "  first paragraph\n\nsecond  \n"
                            "حادث\u200Cو\u200Dملخص"
                            "<b>plain</b> & \\\""
                        ] do
                        Expect.equal
                            (admit value)
                            (Ok value)
                            "No trimming, markup or Unicode rewriting"

                    for value in [ ""; " "; "\n \n" ] do
                        Expect.equal
                            (admit value)
                            (Error Violation.TextRequired)
                            "No fake empty value")
            testCase
                "unsafe controls and directional formatting refuse with ordinary Arabic as control"
                (fun () ->
                    for admit in [ description; summary; action ] do
                        for value in
                            [ "a\rb"; "a\r\nb"; "a\tb"; "a\u001Bb"; "a\u0085b"; "a\u007Fb" ] do
                            Expect.equal
                                (admit value)
                                (Error Violation.ControlCharacters)
                                "Unsafe control"

                        for value in [ "a\u202Eb"; "a\u2066b"; "a\u061Cb"; "a\u200Fb" ] do
                            Expect.equal
                                (admit value)
                                (Error Violation.FormattingCharacter)
                                "Bidi formatting"

                        for value in
                            [ "a\u0000b"; String([| char 0xD800 |]); String([| char 0xDC00 |]) ] do
                            Expect.equal
                                (admit value)
                                (Error Violation.MalformedUnicode)
                                "Not persisted Unicode"

                        Expect.equal
                            (admit "العربية")
                            (Ok "العربية")
                            "Arabic is not unsafe formatting")
            testCase "action is single line with its own 200-scalar boundary" (fun () ->
                let value = String.replicate 200 "😀"
                Expect.equal (action value) (Ok value) "200 scalars, 400 code units"
                Expect.equal (action (value + "a")) (Error(Violation.TooLong 200)) "Own bound"

                Expect.equal
                    (action "first\nsecond")
                    (Error Violation.ControlCharacters)
                    "No notes field"

                Expect.equal (description "first\nsecond") (Ok "first\nsecond") "Narrative control")
            testCase
                "retained syntax has explicit byte limits independent of today's admission"
                (fun () ->
                    let retained = String.replicate 1024 "😀"

                    Expect.equal
                        (IncidentDescription.parse retained
                         |> Result.map IncidentDescription.format)
                        (Ok retained)
                        "4096 UTF8 bytes are representable"

                    Expect.equal
                        (HandlingSummary.parse retained |> Result.map HandlingSummary.format)
                        (Ok retained)
                        "Summary owns its permanent bound"

                    Expect.isError (IncidentDescription.parse (retained + "a")) "4097 bytes refuse"

                    Expect.isError
                        (HandlingSummary.parse (retained + "a"))
                        "Summary overflow refuses"

                    Expect.isError (description retained) "Retained parsing is not admission"
                    let retainedAction = String.replicate 256 "😀"
                    Expect.isOk (ReminderAction.parse retainedAction) "1024-byte action syntax"

                    Expect.isError
                        (ReminderAction.parse (retainedAction + "a"))
                        "1025 bytes refuse"

                    Expect.isError (action retainedAction) "Action parsing is not admission")
            testCase "new profiles do not relax existing party name and reason admission" (fun () ->
                Expect.equal
                    (PartyName.parse " first " |> Result.bind PartyName.admit)
                    (Error Violation.SurroundingWhitespace)
                    "Existing party policy stays distinct"

                Expect.equal
                    (Reason.parse "first\nsecond" |> Result.bind Reason.admit)
                    (Error Violation.ControlCharacters)
                    "Reasons remain single line")
            testCase
                "the former party-name substitute fails the paragraph and 600-scalar witnesses"
                (fun () ->
                    let check parser =
                        for text in
                            [
                                String.replicate 600 "😀"
                                "first\nsecond"
                                "  authored whitespace  "
                            ] do
                            Expect.equal (parser text) (Ok text) "Narrative profile witness"

                    check description
                    check summary

                    let oldName text =
                        PartyName.parse text
                        |> Result.bind PartyName.admit
                        |> Result.map PartyName.format

                    Expect.throws (fun () -> check oldName) "The previous substitute is detected")
        ]
