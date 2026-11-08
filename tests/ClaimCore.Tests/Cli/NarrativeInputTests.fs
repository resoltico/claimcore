module ClaimCore.Tests.NarrativeInputTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

let private accepted =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

[<Tests>]
let tests =
    testList
        "CLI narrative input"
        [
            testCase
                "note commands use text sources without a fabricated expected revision"
                (fun () ->
                    let directory = Directory.CreateTempSubdirectory("claimcore-note-test-")

                    try
                        let path = Path.Combine(directory.FullName, "note.txt")
                        File.WriteAllText(path, "First\r\nSecond", UTF8Encoding(false))

                        let arguments =
                            TextInputs.expand
                                false
                                [ "note"; "add"; "NOTE-1"; "--text-file"; path ]
                            |> accepted

                        let prepared =
                            Grammar.prepare
                                (fun () -> "10000000-0000-7000-8000-000000000071")
                                arguments
                            |> accepted

                        match Frames.value Api.operationExecute prepared.Invocation with
                        | Some(OperationInput.Note { Act = AuthoredNoteAct.AddNote text }) ->
                            Expect.equal
                                text
                                "First\nSecond"
                                "The reviewed text is normalized once"
                        | _ -> failtest "Expected note input"

                        Expect.isTrue
                            (CommandGrammar.supportsAll (
                                CatalogOperation.Query QueryEndpoint.CaseNotes
                            ))
                            "Notes have bounded live traversal"
                    finally
                        directory.Delete true)
            testCase
                "file source normalizes CRLF before preparation and preserves the user file"
                (fun () ->
                    let directory = Directory.CreateTempSubdirectory("claimcore-narrative-test-")

                    try
                        let path = Path.Combine(directory.FullName, "user.txt")
                        let source = "  first\r\nsecond 😀  \r\n"
                        File.WriteAllText(path, source, UTF8Encoding(false))

                        let arguments =
                            TextInputs.expand
                                false
                                [
                                    "work"
                                    "update"
                                    "NAR"
                                    "--handling-summary-file"
                                    path
                                    "--expected-work-revision"
                                    "2"
                                ]
                            |> accepted

                        let prepared =
                            Grammar.prepare
                                (fun () -> "10000000-0000-4000-8000-000000000002")
                                arguments
                            |> accepted

                        match Frames.value Api.operationExecute prepared.Invocation with
                        | Some(OperationInput.Work {
                                                       Act = WorkAct.UpdateWork value
                                                       ExpectedRevision = 2L
                                                   }) ->
                            Expect.equal
                                value.Summary
                                (WorkValue.Set "  first\nsecond 😀  \n")
                                "Exact normalized value freezes"
                        | _ -> failtest "Expected work draft"

                        Expect.equal
                            (File.ReadAllText path)
                            source
                            "Caller-owned source is never rewritten or deleted"
                    finally
                        directory.Delete true)
            testCase
                "text sources are exclusive and unreadable inputs cannot produce a request"
                (fun () ->
                    Expect.isError
                        (TextInputs.expand
                            false
                            [
                                "work"
                                "update"
                                "NAR"
                                "--handling-summary-file"
                                "/does-not-exist"
                            ])
                        "Missing file refuses before dispatch"

                    Expect.isError
                        (TextInputs.expand
                            false
                            [
                                "work"
                                "update"
                                "NAR"
                                "--handling-summary"
                                "kept"
                                "--handling-summary-file"
                                "/does-not-exist"
                            ])
                        "Mixed sources refuse"

                    Expect.isError
                        (TextInputs.expand
                            false
                            [ "work"; "update"; "NAR"; "--handling-summary-editor" ])
                        "Editor requires intentional terminal use")
            testCase "clear is explicit and duplicate set plus clear refuses" (fun () ->
                let prefix = [ "work"; "update"; "NAR"; "--expected-work-revision"; "2" ]

                let prepare arguments =
                    Grammar.prepare (fun () -> "10000000-0000-4000-8000-000000000002") arguments

                let prepared = prepare (prefix @ [ "--clear-handling-summary" ]) |> accepted

                match Frames.value Api.operationExecute prepared.Invocation with
                | Some(OperationInput.Work { Act = WorkAct.UpdateWork value }) ->
                    Expect.equal value.Summary WorkValue.Clear "Null clear is authored"
                | _ -> failtest "Expected work clear"

                Expect.isError
                    (prepare (
                        prefix
                        @ [ "--clear-handling-summary"; "--handling-summary"; "replacement" ]
                    ))
                    "Cannot clear and replace simultaneously")
            testCase
                "terminal paragraphs preserve LF while retained terminal controls remain inert"
                (fun () ->
                    Expect.equal
                        (TerminalText.paragraphs "first\n\nsecond")
                        "first\n\nsecond"
                        "LF is layout"

                    Expect.equal
                        (TerminalText.paragraphs "a\u001B[31m\nb\u0085\u202Ec")
                        "a\\u{001B}[31m\nb\\u{0085}\\u{202E}c"
                        "Each data segment is escaped"

                    Expect.equal
                        (TerminalText.data "first\nsecond")
                        "first\\u{000A}second"
                        "Single-line renderer remains protected")
        ]

[<Tests>]
let editorCleanup =
    testCase "real editor backup cleanup failure refuses before returning authored text" (fun () ->
        let root = Directory.CreateTempSubdirectory "claimcore-editor-control-"
        let report = Path.Combine(root.FullName, "owned-path")
        let editor = Path.Combine(root.FullName, "editor")
        let quote (value: string) = "'" + value.Replace("'", "'\\''") + "'"

        File.WriteAllText(
            editor,
            "#!/bin/sh\nprintf '%s' \"$1\" > "
            + quote report
            + "\nprintf 'private synthetic text' > \"$1\"\nprintf 'synthetic editor backup' > \"$1.backup\"\n"
        )

        File.SetUnixFileMode(
            editor,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        try
            Expect.throws
                (fun () -> TextInputs.editor 8192 "en" editor |> ignore)
                "Backup prevents successful cleanup and text cannot be returned"

            let path = File.ReadAllText report
            Expect.isFalse (File.Exists path) "Primary editor text was removed"

            Expect.isTrue
                (File.Exists(path + ".backup"))
                "The intended failure leaves useful synthetic evidence"

            let directory = Path.GetDirectoryName path |> nonNull

            Expect.equal
                (File.GetUnixFileMode directory)
                (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                "Remaining owned directory stays private"

            Directory.Delete(directory, true)
        finally
            root.Delete true)
