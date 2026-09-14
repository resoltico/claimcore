module ClaimCore.ArchitectureTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore architecture suite"
        [
            FixtureTests.tests
            ProjectEvaluationTests.tests
            ProductGraphTests.tests
            ProductTests.tests
        ]
