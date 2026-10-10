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
        DirectReferencesOnly: bool
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

    let directOnly =
        document.RootElement
            .GetProperty("Properties")
            .GetProperty("DisableTransitiveProjectReferences")
            .GetString()

    {
        DirectReferencesOnly = Boolean.TryParse(directOnly) = (true, true)
        Projects = items.GetProperty("ProjectReference") |> strings "FullPath"
        Packages = items.GetProperty("PackageReference") |> strings "Identity"
        Frameworks = items.GetProperty("FrameworkReference") |> strings "Identity"
    }

let evaluate (projectPath: string) (configuration: string) =
    let source = Path.GetFileNameWithoutExtension projectPath

    if configuration <> "Debug" && configuration <> "Release" then
        invalidArg (nameof configuration) "Architecture evaluation accepts Debug or Release."

    try
        let start = ProcessStartInfo()

        start.FileName <-
            Environment.GetEnvironmentVariable("CLAIMCORE_DOTNET")
            |> Option.ofObj
            |> Option.defaultValue "dotnet"

        start.WorkingDirectory <- nonNull (Path.GetDirectoryName projectPath)
        start.UseShellExecute <- false
        start.CreateNoWindow <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add("msbuild")
        start.ArgumentList.Add(projectPath)

        start.ArgumentList.Add("-getItem:ProjectReference,PackageReference,FrameworkReference")

        start.ArgumentList.Add("-getProperty:DisableTransitiveProjectReferences")
        start.ArgumentList.Add("-property:Configuration=" + configuration)

        let result = ClaimCore.TestSupport.BoundedProcess.run start None (1024 * 1024) 15000
        let body = System.Text.Encoding.UTF8.GetString(result.StandardOutput)

        if result.ExitCode <> 0 then
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
        if not items.DirectReferencesOnly then
            source + " permits implicit transitive project references"

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
