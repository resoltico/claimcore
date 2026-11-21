module ClaimCore.Tests.LikePatternTests

open ClaimCore.Application.Storage
open Expecto

[<Tests>]
let tests =
    testList
        "literal LIKE pattern escaping"
        [
            testCase "doubles a literal backslash" (fun () ->
                Expect.equal (LikePattern.escapeLiteral @"\") @"\\" "Backslash is literal")
            testCase "escapes a percent wildcard" (fun () ->
                Expect.equal (LikePattern.escapeLiteral "%") @"\%" "Percent is literal")
            testCase "escapes an underscore wildcard" (fun () ->
                Expect.equal (LikePattern.escapeLiteral "_") @"\_" "Underscore is literal")
            testCase "escapes adjacent pattern characters exactly once" (fun () ->
                Expect.equal
                    (LikePattern.escapeLiteral @"left\%_right")
                    @"left\\\%\_right"
                    "Literal text and the order of the escaped characters are preserved")
        ]
