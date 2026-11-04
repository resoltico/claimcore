module ClaimCore.Tests.TextTableTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol.Presentation
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
                        [ [ PresentedValue.Money(Inputs.money "1" "EUR") ] ]

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
                let value = PresentedValue.Money(Inputs.money "999999999999999999.9999" "EUR")
                let output = TextTable.rows "en" (options 5 false) columns [ [ value ] ]

                Expect.equal
                    output
                    [ "Amou…"; "999,999,999,999,999,999.9999 EUR" ]
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
