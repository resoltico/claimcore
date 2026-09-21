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
            .LoadNamespacesWithinAssembly(
                typeof<DateTime>.Assembly,
                [| "System"; "System.IO"; "System.Threading" |]
            )
            .LoadNamespacesWithinAssembly(typeof<Console>.Assembly, [| "System" |])
            .LoadNamespacesWithinAssembly(
                typeof<System.Text.Json.Utf8JsonWriter>.Assembly,
                [| "System.Text.Json" |]
            )
            .Build()

    for assembly in assemblies do
        let selector = ArchRuleDefinition.Types().That().ResideInAssembly(assembly)

        if selector.GetObjects(model) |> Seq.isEmpty then
            invalidOp ("Inspection omitted required assembly: " + assembly.GetName().Name)

    model

let private typeName (name: string) = name.Replace("\\,", ",")

let requireTypeNames assemblyName (reflectedNames: string seq) (inspectedNames: string seq) =
    let reflected = reflectedNames |> Seq.map typeName |> Seq.toList
    let inspected = inspectedNames |> Seq.map typeName |> Seq.toList
    let reflectedSet = Set.ofList reflected
    let inspectedSet = Set.ofList inspected

    if reflected.Length <> reflectedSet.Count || inspected.Length <> inspectedSet.Count then
        invalidOp ("Architecture inspection has duplicate type identities in " + assemblyName)

    let missing = Set.difference reflectedSet inspectedSet
    let extra = Set.difference inspectedSet reflectedSet

    if not missing.IsEmpty || not extra.IsEmpty then
        invalidOp (
            $"Architecture inspection is incomplete in {assemblyName}: "
            + $"{missing.Count} missing and {extra.Count} unexpected types."
        )

let requireCompleteTypes (architecture: Architecture) (assembly: System.Reflection.Assembly) =
    let name = nonNull (assembly.GetName().Name)

    let reflected =
        try
            assembly.GetTypes() |> Array.map (fun item -> nonNull item.FullName)
        with :? ReflectionTypeLoadException ->
            invalidOp ("Reflection could not enumerate required implementation types in " + name)

    let inspected =
        architecture.Types
        |> Seq.filter (fun item -> item.Assembly.Name = name)
        |> Seq.map _.FullName

    requireTypeNames name reflected inspected

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
