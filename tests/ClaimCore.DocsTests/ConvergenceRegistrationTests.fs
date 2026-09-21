module ClaimCore.DocsTests.ConvergenceRegistrationTests

open ClaimCore.Docs
open Expecto

let private original = "dotnet:Original", "dotnet-mtp", "original.json"
let private added = "dotnet:Architecture", "dotnet-mtp", "architecture.json"

let private registrationTests =
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

/// A suite that runs in CI but is never reconciled as evidence is worse than one that does not run:
/// it reads as covered. The manifest classifies which assemblies publish a compiled inventory, so
/// every one of them must also be a required report somewhere in the stage catalog.
let private inventoriedSuitesAreReconciled =
    testCase "every inventoried test assembly is required evidence in some stage" (fun () ->
        let inventoried =
            ArchitectureManifest.current.Value
            |> ArchitectureManifest.testInventoryAssemblies

        let reconciled = Stages.testReports |> List.map _.Assembly |> Set.ofList

        Expect.isEmpty
            (Set.difference inventoried reconciled)
            "An inventoried suite produces no required TRX report"

        Expect.isEmpty
            (Set.difference reconciled inventoried)
            "A required TRX report names an assembly the manifest does not inventory")

/// Every required report must also have a stage that actually produces it.
let private requiredReportsHaveProducers =
    testCase "every required report names a registered stage" (fun () ->
        let stages = Stages.definitions |> List.map _.Id |> Set.ofList

        for report in Stages.testReports do
            Expect.isTrue
                (Set.contains report.StageId stages)
                ("A required report has no producing stage: " + report.StageId))

let tests =
    testList
        "convergence registration and evidence coverage"
        [
            registrationTests
            inventoriedSuitesAreReconciled
            requiredReportsHaveProducers
        ]
