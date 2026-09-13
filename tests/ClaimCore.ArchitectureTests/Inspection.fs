module ClaimCore.ArchitectureTests.Inspection

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open ArchUnitNET.Domain
open ArchUnitNET.Fluent
open ArchUnitNET.Loader

let requireFile directory name =
    let path = Path.Combine(directory, name + ".dll")

    if not (File.Exists path) then
        invalidOp ("Required inspection input is missing: " + name)

    path

let private requireImplementation (assembly: System.Reflection.Assembly) =
    if assembly.IsDefined(typeof<ReferenceAssemblyAttribute>, false) then
        invalidOp "Reference assemblies cannot establish method-body dependencies."

    let isDebug =
        assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
        |> Option.ofObj
        |> Option.exists (fun attribute -> attribute.Configuration = "Debug")

    if not isDebug then
        invalidOp "Architecture inspection requires the fresh Debug implementation build."

    let unoptimised =
        assembly.GetCustomAttribute<DebuggableAttribute>()
        |> Option.ofObj
        |> Option.exists _.IsJITOptimizerDisabled

    if not unoptimised then
        invalidOp "Architecture inspection requires disabled optimisation."

let loadRequired directory (names: string list) =
    if List.isEmpty names || names.Length <> (Set.ofList names).Count then
        invalidArg (nameof names) "Inspection inputs must be nonempty and unique."

    names
    |> List.map (fun name ->
        let assembly = Assembly.LoadFrom(requireFile directory name)

        if assembly.GetName().Name <> name then
            invalidOp ("Inspection assembly identity mismatch: " + name)

        requireImplementation assembly
        assembly)
    |> List.toArray

let build (assemblies: System.Reflection.Assembly array) =
    let model =
        ArchLoader()
            .WithoutArchitectureCache()
            .WithoutRuleEvaluationCache()
            .LoadAssemblies(assemblies)
            .LoadNamespacesWithinAssembly(typeof<DateTime>.Assembly, [| "System"; "System.IO" |])
            .LoadNamespacesWithinAssembly(typeof<Console>.Assembly, [| "System" |])
            .Build()

    for assembly in assemblies do
        let selector = ArchRuleDefinition.Types().That().ResideInAssembly(assembly)

        if selector.GetObjects(model) |> Seq.isEmpty then
            invalidOp ("Inspection omitted required assembly: " + assembly.GetName().Name)

    model

let requireSelection (architecture: Architecture) (selection: IObjectProvider<'T>) =
    let found = selection.GetObjects(architecture) |> Seq.toList

    if found.IsEmpty then
        invalidOp ("Required architecture selector is empty: " + selection.Description)

    found

let violations (architecture: Architecture) (rule: IArchRule) =
    rule.Evaluate(architecture)
    |> Seq.filter (fun result -> not result.Passed)
    |> Seq.map _.Description
    |> Seq.toList

let check architecture rule =
    match violations architecture rule with
    | [] -> ()
    | failures -> Expecto.Tests.failtest (String.concat Environment.NewLine failures)
