module ClaimCore.ArchitectureTests.ProductGraphTests

open System
open System.IO
open Expecto
open ClaimCore.TestSupport

let private evaluatedGraph () =
    let projects =
        Path.Combine(RepositoryRoot.find (), "src") |> ProjectReferences.loadProjects

    let names = ProjectReferences.names projects

    let failures =
        [ "Debug"; "Release" ]
        |> List.collect (fun configuration ->
            projects
            |> List.collect (fun path ->
                let items = ProjectEvaluation.evaluate path configuration

                ProjectEvaluation.violations names ProductPolicy.allowed path items
                |> List.map (fun failure -> configuration + ": " + failure)))

    if not failures.IsEmpty then
        failtest (String.concat Environment.NewLine failures)

let private inspectedReport () =
    let model = ProductModel.architecture.Value

    for assembly in ProductModel.assemblies.Value do
        Inspection.requireCompleteTypes model assembly

    let report = InspectionReport.create ProductPolicy.names model
    let bytes = InspectionReport.encode report

    Expect.equal
        report.Assemblies.Length
        ProductPolicy.names.Length
        "The report must cover every classified product assembly"

    InspectionReport.writeRequired bytes

let tests =
    testList
        "product graph evidence"
        [
            testCase "evaluated Debug and Release dependencies obey component policy" evaluatedGraph
            testCase "compiled model covers every type and emits a bounded graph" inspectedReport
        ]
