module ClaimCore.Tests.TextTableTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private column key label prominence index : EntryDefinition<PresentedValue list> =
    {
        Key = key
        LabelId = label
        Prominence = prominence
        IsMoney = key = "money"
        Read = List.item index
    }

let private options width bold =
    {
        TextTableOptions.Width = width
        BoldHeader = bold
    }

[<Tests>]
let tests =
    testList
        "typed text table layout"
        [
            testCase "lower prominence disappears before primary columns" (fun () ->
                let columns =
                    [
                        column "primary" "label.action" Prominence.Primary 0
                        column "detail" "label.before" Prominence.Detail 1
                        column "secondary" "label.after" Prominence.Secondary 2
                    ]

                let values =
                    [
                        [
                            PresentedValue.Text "PRIMARY"
                            PresentedValue.Text "DETAIL"
                            PresentedValue.Text "SECOND"
                        ]
                    ]

                Expect.equal
                    (TextTable.rows "en" (options 20 false) columns values)
                    [ "PRIMARY   SECOND" ]
                    "Detail goes first while Secondary still fits"

                Expect.equal
                    (TextTable.rows "en" (options 12 false) columns values)
                    [ "PRIMARY" ]
                    "Secondary goes next and Primary remains")
            testCase "money is aligned by typed value rather than string shape" (fun () ->
                let columns = [ column "money" "field.claimedAmount.label" Prominence.Primary 0 ]

                let rows =
                    TextTable.rows
                        "en"
                        (options 50 false)
                        columns
                        [ [ PresentedValue.money (Inputs.money "1" "EUR") ] ]

                Expect.equal
                    rows
                    [ "                        1.00 EUR" ]
                    "A bounded full-width money cell pads on the left"

                Expect.equal
                    (TextTable.rows
                        "en"
                        (options 50 false)
                        columns
                        [ [ PresentedValue.Text "1.00 EUR" ] ])
                    [ "1.00 EUR" ]
                    "An unrelated text value is not misclassified as money")
            testCase "monetary precision is never truncated when a table cannot fit" (fun () ->
                let columns = [ column "money" "field.claimedAmount.label" Prominence.Primary 0 ]
                let value = PresentedValue.money (Inputs.money "999999999999999999.9999" "EUR")
                let output = TextTable.rows "en" (options 5 false) columns [ [ value ] ]

                Expect.equal
                    output
                    [ "Clai…"; "999,999,999,999,999,999.9999 EUR" ]
                    "Narrow cards preserve every monetary digit for terminal wrapping"

                let wide = TextTable.rows "en" (options 50 false) columns [ [ value ] ]

                Expect.equal
                    wide
                    [ "999,999,999,999,999,999.9999 EUR" ]
                    "The reserved maximum width retains the complete amount")
            testCase "truncation never splits an escape or a grapheme" (fun () ->
                let escaped = TerminalText.data "A\u001bB\u202eC"

                Expect.equal
                    (TextTable.truncate 10 escaped)
                    "A\\u{001B}…"
                    "The entire escaped control remains visible"

                Expect.equal
                    (TextTable.truncate 6 escaped)
                    "A…"
                    "A large escape is omitted atomically"

                Expect.equal
                    (TextTable.truncate 3 (TerminalText.data "X\\Y"))
                    "X…"
                    "A literal doubled backslash is indivisible"

                Expect.equal (TextTable.truncate 2 "éQZ") "é…" "A combining mark stays attached")
            testCase "header styling is an explicit boundary policy" (fun () ->
                let columns = [ column "action" "label.action" Prominence.Primary 0 ]

                Expect.equal
                    (TextTable.header "en" (options 30 false) columns)
                    "Action"
                    "Redirected and NO_COLOR output has no ANSI style"

                Expect.equal
                    (TextTable.header "en" (options 30 true) columns)
                    "\u001b[1mAction\u001b[0m"
                    "Only the permitted header receives trusted ANSI")
            testCase "narrow layouts retain every primary field as a labelled card" (fun () ->
                let columns =
                    [
                        column "one" "label.before" Prominence.Primary 0
                        column "two" "label.after" Prominence.Primary 1
                    ]

                let output =
                    TextTable.rows
                        "en"
                        (options 5 false)
                        columns
                        [ [ PresentedValue.Text "ONE"; PresentedValue.Text "TWO" ] ]

                Expect.equal
                    output
                    [ "Befo…"; "ONE"; "After"; "TWO" ]
                    "Primary values survive the card fallback"

                Expect.isTrue
                    (output |> List.forall (fun line -> TextTable.width line <= 5))
                    "Every line respects the narrow width")
        ]


[<Tests>]
let terminalCells =
    testList
        "terminal cell width"
        [
            testCase
                "CJK emoji and combining clusters use independently known cell counts"
                (fun () ->
                    for text, expected in
                        [
                            "山田太郎🚗", 10
                            "山", 2
                            "🚗", 2
                            "👩‍👩‍👦", 2
                            "👍🏽", 2
                            "🇱🇻", 2
                            "1️⃣", 2
                            "ā", 1
                            "عادل", 4
                        ] do
                        Expect.equal
                            (TextTable.width text)
                            expected
                            "Cell counts are literal expectations, not self-measurement")
            testCase "wide clusters and escaped controls remain atomic under clipping" (fun () ->
                Expect.equal
                    (TextTable.truncate 5 "山田太郎🚗")
                    "山田…"
                    "Two wide glyphs plus one cell ellipsis"

                Expect.equal (TextTable.truncate 3 "👩‍👩‍👦AB") "👩‍👩‍👦…" "A family emoji cannot split at ZWJ"

                Expect.equal
                    (TextTable.truncate 2 "āBC")
                    "ā…"
                    "Combining marks stay with their base"

                let escaped = TerminalText.data @"山\u{001B}🚗"

                Expect.equal
                    (TextTable.truncate 5 escaped)
                    ("山" + @"\\" + "…")
                    "Literal escape-like text keeps its doubled backslash atom"

                Expect.equal
                    (TextTable.truncate 9 (TerminalText.data (string (char 0x1B) + "AB")))
                    (@"\u{001B}" + "…")
                    "A data escape is one indivisible layout atom")
            testCase
                "mixed wide and narrow names align the next column in every language"
                (fun () ->
                    let columns =
                        [
                            column "name" "label.before" Prominence.Primary 0
                            column "next" "label.after" Prominence.Primary 1
                        ]

                    let rows =
                        [
                            [ PresentedValue.Text "山田太郎🚗"; PresentedValue.Text "END" ]
                            [ PresentedValue.Text "Ann"; PresentedValue.Text "END" ]
                        ]

                    for language in [ "en"; "lv"; "ar" ] do
                        Expect.equal
                            (TextTable.rows language (options 100 false) columns rows)
                            [ "山田太…   END"; "Ann       END" ]
                            "Literal rows place END at terminal cell eleven for both names")
            testCase
                "East Asian dates stay Gregorian and exact money retains all decimals"
                (fun () ->
                    for language, expected in
                        [
                            "ko", "2026년 11월 6일"
                            "ja", "2026年11月6日"
                            "zh-Hans", "2026年11月6日"
                            "zh-Hant", "2026年11月6日"
                        ] do
                        Expect.equal
                            (ReplyTextFormat.calendarDate language (System.DateOnly(2026, 11, 6)))
                            expected
                            "Culture-appropriate Gregorian order"

                        Expect.equal
                            (ReplyTextFormat.money language (Inputs.money "12345.6789" "EUR"))
                            "12,345.6789 EUR"
                            "Canonical fractional text is exact"

                    for language in [ "de"; "es" ] do
                        Expect.equal
                            (ReplyTextFormat.money language (Inputs.money "12345.6789" "EUR"))
                            "12.345,6789 EUR"
                            "Grouping does not change the amount"

                    Expect.equal (TextTable.width "담당자") 6 "Hangul occupies two cells per syllable")
        ]
