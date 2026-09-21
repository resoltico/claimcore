module ClaimCore.ArchitectureTests.ProductGraphTests

open System
open Expecto
open ClaimCore.TestSupport

let private ordinal (left: string) right =
    StringComparer.Ordinal.Compare(left, right)

/// MSBuild evaluation is the authority on what a project really links, including edges introduced
/// by an import or a configuration condition. Shipped and tooling components are both evaluated.
let private evaluatedGraph () =
    let all = RepositoryRoot.find () |> ProjectReferences.loadRepositoryProjects
    let names = ProjectReferences.names all

    let packages =
        ProductPolicy.components
        |> List.map (fun item -> item.Name, item.Packages)
        |> Map.ofList

    let frameworks =
        ProductPolicy.components
        |> List.map (fun item -> item.Name, item.FrameworkReferences)
        |> Map.ofList

    let evaluated =
        ProductPolicy.components
        |> List.filter (fun item -> item.Tier <> "test")
        |> List.map _.Name
        |> Set.ofList

    let failures =
        [ "Debug"; "Release" ]
        |> List.collect (fun configuration ->
            all
            |> List.filter (fun path ->
                evaluated.Contains(nonNull (IO.Path.GetFileNameWithoutExtension path)))
            |> List.collect (fun path ->
                let items = ProjectEvaluation.evaluate path configuration

                ProjectEvaluation.violations
                    names
                    ProductPolicy.allowed
                    packages
                    frameworks
                    path
                    items
                |> List.map (fun failure -> configuration + ": " + failure)))

    if not failures.IsEmpty then
        failtest (failures |> List.sortWith ordinal |> String.concat Environment.NewLine)

/// A permitted edge that nothing actually uses is a stale permission that will outlive the reason
/// it was granted. For the product tier the compiled model knows the truth, so the reviewed graph
/// and the observed graph must be the same graph.
let private observedEdgesMatchManifest () =
    let model = ProductModel.architecture.Value
    let product = ProductPolicy.names |> Set.ofList

    let observed =
        model.Types
        |> Seq.filter (fun item -> product.Contains item.Assembly.Name)
        |> Seq.collect (fun item ->
            item.Dependencies
            |> Seq.map (fun dependency -> item.Assembly.Name, dependency.Target.Assembly.Name))
        |> Seq.filter (fun (source, target) -> source <> target && product.Contains target)
        |> Set.ofSeq

    let permitted =
        ProductPolicy.product
        |> Seq.collect (fun item -> item.DependsOn |> List.map (fun target -> item.Name, target))
        |> Set.ofSeq

    let failures =
        [
            for source, target in Set.difference observed permitted |> Set.toList |> List.sort do
                source + " depends on unpermitted " + target

            for source, target in Set.difference permitted observed |> Set.toList |> List.sort do
                source + " is permitted " + target + " but never uses it"
        ]

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
            testCase "every permitted product edge is actually used" observedEdgesMatchManifest
            testCase "compiled model covers every type and emits a bounded graph" inspectedReport
        ]
