module ClaimCore.ArchitectureTests.ProjectEvaluation

open System
open System.Diagnostics
open System.IO
open System.Text.Json

type Items =
    {
        Projects: string list
        Packages: string list
        Frameworks: string list
    }

let private strings (property: string) (items: JsonElement) =
    items.EnumerateArray()
    |> Seq.map (fun item ->
        let value = item.GetProperty(property).GetString()

        if String.IsNullOrWhiteSpace value then
            invalidOp "Evaluated dependency item has incomplete metadata."

        nonNull value)
    |> Seq.toList

let private parse (output: string) =
    if output.Length > 1024 * 1024 then
        invalidOp "Evaluated dependency graph exceeds its output limit."

    use document = JsonDocument.Parse(output)
    let items = document.RootElement.GetProperty("Items")

    {
        Projects = items.GetProperty("ProjectReference") |> strings "FullPath"
        Packages = items.GetProperty("PackageReference") |> strings "Identity"
        Frameworks = items.GetProperty("FrameworkReference") |> strings "Identity"
    }

let evaluate (projectPath: string) (configuration: string) =
    let source = Path.GetFileNameWithoutExtension projectPath

    if configuration <> "Debug" && configuration <> "Release" then
        invalidArg (nameof configuration) "Architecture evaluation accepts Debug or Release."

    try
        use child = new Process()
        child.StartInfo.FileName <- "dotnet"
        child.StartInfo.WorkingDirectory <- nonNull (Path.GetDirectoryName projectPath)
        child.StartInfo.UseShellExecute <- false
        child.StartInfo.CreateNoWindow <- true
        child.StartInfo.RedirectStandardOutput <- true
        child.StartInfo.RedirectStandardError <- true
        child.StartInfo.ArgumentList.Add("msbuild")
        child.StartInfo.ArgumentList.Add(projectPath)

        child.StartInfo.ArgumentList.Add(
            "-getItem:ProjectReference,PackageReference,FrameworkReference"
        )

        child.StartInfo.ArgumentList.Add("-property:Configuration=" + configuration)

        if not (child.Start()) then
            invalidOp "MSBuild evaluation did not start."

        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(15000)) then
            child.Kill(true)
            invalidOp "MSBuild evaluation timed out."

        let body = output.GetAwaiter().GetResult()
        errors.GetAwaiter().GetResult() |> ignore

        if child.ExitCode <> 0 then
            invalidOp "MSBuild evaluation failed."

        parse body
    with _ ->
        invalidOp ("Evaluated dependency inspection failed for " + source + " " + configuration)

/// MSBuild-evaluated edges must match the reviewed permission, package, and shared-framework sets
/// exactly, in the configuration under evaluation. FSharp.Core and Microsoft.NETCore.App reach
/// every .NET project through the repository build and are therefore not reviewed per component;
/// any other shared framework, such as ASP.NET Core, is an architecture decision.
let violations
    (projectNames: Map<string, string>)
    (permissions: Map<string, string list>)
    (packages: Map<string, string list>)
    (frameworks: Map<string, string list>)
    (projectPath: string)
    (items: Items)
    =
    let ordinal (left: string) right =
        StringComparer.Ordinal.Compare(left, right)

    let source = nonNull (Path.GetFileNameWithoutExtension projectPath)
    let allowed = permissions |> Map.find source |> Set.ofList
    let approved = packages |> Map.find source |> Set.ofList
    let shared = frameworks |> Map.find source |> Set.ofList

    let unclassified, declared =
        items.Projects
        |> List.map (fun path -> Map.tryFind (Path.GetFullPath path) projectNames)
        |> List.partition Option.isNone

    let declaredSet = declared |> List.choose id |> Set.ofList

    let declaredPackages =
        items.Packages
        |> List.filter (fun package -> package <> "FSharp.Core")
        |> Set.ofList

    let declaredFrameworks =
        items.Frameworks
        |> List.filter (fun framework -> framework <> "Microsoft.NETCore.App")
        |> Set.ofList

    [
        if not unclassified.IsEmpty then
            source + " declares an unclassified evaluated project reference"

        for surplus in Set.difference declaredSet allowed |> Set.toList |> List.sortWith ordinal do
            source + " declares a forbidden evaluated project reference: " + surplus

        for stale in Set.difference allowed declaredSet |> Set.toList |> List.sortWith ordinal do
            source + " is permitted an undeclared evaluated project reference: " + stale

        for surplus in
            Set.difference declaredPackages approved |> Set.toList |> List.sortWith ordinal do
            source + " declares an unapproved evaluated package reference: " + surplus

        for stale in Set.difference approved declaredPackages |> Set.toList |> List.sortWith ordinal do
            source + " is approved an undeclared evaluated package reference: " + stale

        for surplus in
            Set.difference declaredFrameworks shared |> Set.toList |> List.sortWith ordinal do
            source + " declares an unapproved evaluated framework reference: " + surplus

        for stale in Set.difference shared declaredFrameworks |> Set.toList |> List.sortWith ordinal do
            source + " is approved an undeclared evaluated framework reference: " + stale
    ]
