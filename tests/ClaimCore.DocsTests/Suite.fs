module ClaimCore.DocsTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore documentation assurance"
        [
            MarkdownTests.tests
            PathAndLinkTests.tests
            ManifestTests.tests
            EvidenceTests.tests
            FrontendReportInventoryTests.tests
            ReviewTests.tests
            ConvergenceRegistrationTests.tests
        ]
