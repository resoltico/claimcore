module ClaimCore.Tests.ShellTokenizerTests

open Expecto
open ClaimCore.Cli

let private words () =
    for input, expected in
        [
            "", []
            " \t ", []
            "case get REF", [ "case"; "get"; "REF" ]
            "case\tget\tREF", [ "case"; "get"; "REF" ]
            "case open '😀 العربية'", [ "case"; "open"; "😀 العربية" ]
            "case open 'MY REF' --claimant-name \"A B\"",
            [ "case"; "open"; "MY REF"; "--claimant-name"; "A B" ]
            "a''b \"\" ''", [ "ab"; ""; "" ]
            "a\\ b 'a\\b'", [ "a b"; "a\\b" ]
            "\"a\\qb\" \"a\\\"b\"", [ "a\\qb"; "a\"b" ]
            "x; rm | cat && $(touch marker) `command` * ~ $HOME > file",
            [
                "x;"
                "rm"
                "|"
                "cat"
                "&&"
                "$(touch"
                "marker)"
                "`command`"
                "*"
                "~"
                "$HOME"
                ">"
                "file"
            ]
            "\"$HOME $(command) `command` *\"", [ "$HOME $(command) `command` *" ]
        ] do
        Expect.equal
            (ShellTokenizer.split input)
            (Ok expected)
            "Literal independently authored token sequence"

let private incomplete () =
    for source in [ "'"; "\""; "a 'b"; "a \"b"; "a\\"; "\"a\\" ] do
        Expect.isError (ShellTokenizer.split source) "Incomplete quoting never becomes a command"

[<Tests>]
let tests =
    testList
        "shell tokenizer"
        [
            testCase "quotes escapes empty words and metacharacters remain literal" words
            testCase "unterminated quotes and trailing escapes are usage errors" incomplete
        ]
