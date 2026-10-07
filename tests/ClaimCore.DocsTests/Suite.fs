module ClaimCore.DocsTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore documentation assurance"
        [
            MarkdownTests.tests
            ExecutableHelpTests.tests
            PathAndLinkTests.tests
            ContractTokenTests.tests
        ]
