module ClaimCore.DocsTests.ConvergenceRegistrationTests

open ClaimCore.Docs
open Expecto

let private original = "dotnet:Original", "dotnet-mtp", "original.json"
let private added = "dotnet:Architecture", "dotnet-mtp", "architecture.json"

let tests =
    testList
        "immutable source registrations"
        [
            testCase "new live producer does not rewrite historical registration" (fun () ->
                Expect.isTrue
                    (ConvergenceBaseline.registrationsPreserved [ original ] [ original; added ])
                    "A new required producer can extend the current inventory")
            testCase "new producer cannot replace a missing historical producer" (fun () ->
                Expect.isFalse
                    (ConvergenceBaseline.registrationsPreserved [ original ] [ added ])
                    "A historical producer cannot disappear")
            testCase "historical kind and inventory identity cannot change" (fun () ->
                let renamedKind = "dotnet:Original", "other-driver", "original.json"
                let renamedPath = "dotnet:Original", "dotnet-mtp", "replacement.json"

                for changed in [ renamedKind; renamedPath ] do
                    Expect.isFalse
                        (ConvergenceBaseline.registrationsPreserved [ original ] [ changed; added ])
                        "The complete historical registration remains binding")
        ]
