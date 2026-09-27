module ClaimCore.ArchitectureTests.ProductGraphTests

open System
open System.IO
open System.Reflection
open Expecto
open ClaimCore.TestSupport
open Microsoft.FSharp.Reflection

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
let private assemblyReferences =
    ProductModel.assemblies.Value
    |> Array.map (fun assembly ->
        let source = assembly.GetName().Name |> Option.ofObj |> Option.defaultValue ""

        let targets =
            assembly.GetReferencedAssemblies()
            |> Array.choose (fun item -> Option.ofObj item.Name)
            |> Set.ofArray

        source, targets)
    |> Map.ofArray

let private domainSignatureClosureIsUsed () =
    let postgres =
        ProductModel.assemblies.Value
        |> Array.find (fun item -> item.GetName().Name = "ClaimCore.Postgres")

    let caseField unionName caseName expectedType =
        let unionType =
            postgres.GetType("ClaimCore.Postgres." + unionName, true)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Reviewed Postgres union is missing.")

        FSharpType.GetUnionCases(unionType, BindingFlags.Public ||| BindingFlags.NonPublic)
        |> Array.exists (fun case ->
            case.Name = caseName
            && (case.GetFields()
                |> Array.exists (fun field -> field.PropertyType.FullName = expectedType)))

    let sourceUses path (symbol: string) =
        File
            .ReadAllText(Path.Combine(RepositoryRoot.find (), path))
            .Contains(symbol, StringComparison.Ordinal)

    caseField "OwnerWitnessPruneOutcome" "Refused" "ClaimCore.Domain.LifecycleRefusal"
    && caseField "OwnerTerminalOutcome" "Advanced" "ClaimCore.Domain.PrivacyPhase"
    && sourceUses
        "src/ClaimCore.Database/DatabasePruneWitnessExecution.fs"
        "OwnerWitnessPruneOutcome"
    && sourceUses
        "src/ClaimCore.Database/DatabaseTerminalCopyAbsenceExecution.fs"
        "OwnerTerminalOutcome"

let private edgeExample (model: ArchUnitNET.Domain.Architecture) source target =
    model.Types
    |> Seq.tryPick (fun item ->
        if item.Assembly.Name <> source then
            None
        else
            item.Dependencies
            |> Seq.tryFind (fun edge -> edge.Target.Assembly.Name = target)
            |> Option.map (fun edge -> " via " + item.FullName + " -> " + edge.Target.FullName))
    |> Option.defaultValue ""

let private observedEdgesMatchManifest () =
    let model = ProductModel.architecture.Value
    let product = ProductPolicy.names |> Set.ofList

    let observed =
        model.Types
        |> Seq.filter (fun item -> product.Contains item.Assembly.Name)
        |> Seq.collect (fun item ->
            item.Dependencies
            |> Seq.map (fun dependency -> item.Assembly.Name, dependency.Target.Assembly.Name))
        // ArchUnit can attribute a framework generic (FSharpMap/Option) to an unrelated
        // product method using that generic. A real cross-assembly IL dependency must also
        // appear in the source assembly's metadata AssemblyRef table.
        |> Seq.filter (fun (source, target) ->
            source <> target
            && product.Contains target
            && (Map.find source assemblyReferences).Contains target)
        |> Set.ofSeq

    let permitted =
        ProductPolicy.product
        |> Seq.collect (fun item -> item.DependsOn |> List.map (fun target -> item.Name, target))
        |> Set.ofSeq

    let compileOnly =
        ProductPolicy.product
        |> Seq.collect (fun item ->
            item.CompileOnlyDependsOn |> List.map (fun target -> item.Name, target))
        |> Set.ofSeq

    let expectedRuntime = Set.difference permitted compileOnly

    for source, target in compileOnly do
        match source, target with
        | "ClaimCore.Database", "ClaimCore.Domain" when
            not ((Map.find source assemblyReferences).Contains target)
            && domainSignatureClosureIsUsed ()
            ->
            ()
        | _ ->
            failtest (source + " has no reviewed compile-only type-closure evidence for " + target)

    let failures =
        [
            for source, target in Set.difference observed expectedRuntime |> Set.toList |> List.sort do
                source + " depends on unpermitted " + target + edgeExample model source target

            for source, target in Set.difference expectedRuntime observed |> Set.toList |> List.sort do
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
