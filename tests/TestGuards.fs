module ClaimCore.TestSupport.TestGuards

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Xml.Linq
open Expecto

let private publicStatic (value: MemberInfo) =
    match value with
    | :? PropertyInfo as property ->
        match property.GetGetMethod true with
        | null -> false
        | getter -> getter.IsPublic && getter.IsStatic
    | :? FieldInfo as field -> field.IsPublic && field.IsStatic
    | _ -> false

let private testMembers (subject: Type) =
    let flags =
        BindingFlags.Public
        ||| BindingFlags.NonPublic
        ||| BindingFlags.Static
        ||| BindingFlags.Instance
        ||| BindingFlags.DeclaredOnly

    let properties =
        subject.GetProperties flags
        |> Array.filter (fun value ->
            value.PropertyType = typeof<Test>
            && (value.IsDefined(typeof<TestsAttribute>, false) || publicStatic value))
        |> Array.map (fun value -> value :> MemberInfo)

    // Private fields are storage/helpers, not the runner's supported public field entrypoints.
    let fields =
        subject.GetFields flags
        |> Array.filter (fun value -> value.FieldType = typeof<Test> && value.IsPublic)
        |> Array.map (fun value -> value :> MemberInfo)

    Array.append properties fields

let undiscoveredTests (assembly: Assembly) =
    assembly.GetTypes()
    |> Array.collect testMembers
    |> Array.filter (fun value ->
        not (
            (value.DeclaringType |> nonNull).IsVisible
            && publicStatic value
            && value.IsDefined(typeof<TestsAttribute>, false)
        ))
    |> Array.map (fun value ->
        let name = (value.DeclaringType |> nonNull).FullName |> nonNull
        name + "." + value.Name)

let requiredAttribute name (item: XElement) =
    match item.Attribute(XName.Get name) with
    | null -> invalidOp ("Missing project attribute: " + name)
    | attribute -> attribute.Value

let private compileItems configuration project =
    let start =
        ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true)

    for argument in
        [
            "msbuild"
            project
            "-getItem:Compile"
            "-property:Configuration=" + configuration
            "-nodeReuse:false"
        ] do
        start.ArgumentList.Add argument

    use child = new Process(StartInfo = start)

    if not (child.Start()) then
        invalidOp ("Could not evaluate " + project)

    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        child.WaitForExit()
        invalidOp ("Compile item evaluation timed out: " + project)

    errors.GetAwaiter().GetResult() |> ignore

    if child.ExitCode <> 0 then
        invalidOp ("Compile item evaluation failed: " + project)

    output.GetAwaiter().GetResult()

let sourceMembership configuration (project: string) =
    let directory =
        Path.GetDirectoryName(Path.GetFullPath project)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "A project must have a parent directory.")

    use items = JsonDocument.Parse(compileItems configuration project)

    let compiled =
        items.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("FullPath").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp ("Compile item has no path: " + project)))
        |> Set.ofSeq

    let sources =
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            Path.GetExtension path = ".fs" || Path.GetExtension path = ".fsi")
        |> Seq.map Path.GetFullPath
        |> Set.ofSeq

    let uncompiled = Set.difference sources compiled |> Set.toList
    let missing = compiled |> Set.filter (File.Exists >> not) |> Set.toList
    uncompiled, missing

let rec completeTests =
    function
    | TestCase(_, focus) -> focus = Normal
    | TestList(children, focus) -> focus = Normal && List.forall completeTests children
    | TestLabel(_, child, focus) -> focus = Normal && completeTests child
    | Test.Sequenced(_, child) -> completeTests child
