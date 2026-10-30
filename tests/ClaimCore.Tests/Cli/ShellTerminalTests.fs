module ClaimCore.Tests.ShellTerminalTests

open System
open Expecto
open ClaimCore.Cli

[<Tests>]
let tests =
    testList
        "shell terminal keys"
        [
            testCase "Unix EOF needs no virtual key or Control metadata" (fun () ->
                let eof = ConsoleKeyInfo('\u0004', enum<ConsoleKey> 0, false, false, false)

                Expect.isTrue
                    (ShellKeys.isEndOfInput true eof)
                    "Unix ReadKey may return only its configured EOF character")
            testCase "EOF terminates only an empty prompt" (fun () ->
                let unix = ConsoleKeyInfo('\u0004', enum<ConsoleKey> 0, false, false, false)
                let control = ConsoleKeyInfo('\u0004', ConsoleKey.D, false, false, true)
                let ordinary = ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)

                Expect.isTrue (ShellKeys.isEndOfInput true control) "Control-D remains supported"
                Expect.isFalse
                    (ShellKeys.isEndOfInput false unix)
                    "A Unix EOF cannot discard a draft"

                Expect.isFalse
                    (ShellKeys.isEndOfInput false control)
                    "Control-D cannot discard a draft"

                Expect.isFalse (ShellKeys.isEndOfInput true ordinary) "An ordinary D is text")
        ]
