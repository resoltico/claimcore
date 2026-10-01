module ClaimCore.DocsTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore documentation assurance"
        [ MarkdownTests.tests; PathAndLinkTests.tests; ContractTokenTests.tests ]
