module ClaimCore.Tests.NotesTests

open System
open Expecto
open ClaimCore.Domain

[<Tests>]
let tests =
    testList
        "note text values"
        [
            testCase
                "text admits 2000 Unicode scalars including LF and supplementary characters"
                (fun () ->
                    let value = String.replicate 1998 "😀" + "\nA"

                    Expect.equal
                        (NoteText.parse value
                         |> Result.bind NoteText.admit
                         |> Result.map NoteText.format)
                        (Ok value)
                        "Scalars, not UTF-16 code units"

                    Expect.isError
                        (NoteText.parse (String.replicate 2001 "A") |> Result.bind NoteText.admit)
                        "Scalar ceiling"

                    Expect.isError
                        (NoteText.parse (String.replicate 2049 "😀"))
                        "UTF-8 byte ceiling"

                    Expect.isError (NoteText.parse "") "At least one scalar"

                    Expect.isError
                        (NoteText.parse "line\rbreak" |> Result.bind NoteText.admit)
                        "Only LF is permitted"

                    Expect.equal
                        (NoteText.parse " indented"
                         |> Result.bind NoteText.admit
                         |> Result.map NoteText.format)
                        (Ok " indented")
                        "Narrative indentation is preserved")
        ]
