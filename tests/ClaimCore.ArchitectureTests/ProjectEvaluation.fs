module ClaimCore.ArchitectureTests.ProjectEvaluation

open System
open System.Diagnostics
open System.IO
open System.Text.Json

type Items =
    {
        Projects: string list
        Packages: string list
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
        child.StartInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference")
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

let violations
    (projectNames: Map<string, string>)
    (permissions: Map<string, string list>)
    (projectPath: string)
    (items: Items)
    =
    let source = nonNull (Path.GetFileNameWithoutExtension projectPath)
    let allowed = permissions |> Map.find source

    let projectFailures =
        items.Projects
        |> List.choose (fun path ->
            match Map.tryFind (Path.GetFullPath path) projectNames with
            | None -> Some(source + " declares an unclassified evaluated project reference")
            | Some target when not (List.contains target allowed) ->
                Some(source + " declares a forbidden evaluated project reference: " + target)
            | Some _ -> None)

    let packageFailures =
        items.Packages
        |> List.choose (fun package ->
            if
                package = "FSharp.Core" || (source = "ClaimCore.Postgres" && package = "Npgsql")
            then
                None
            elif package = "Npgsql" then
                Some(source + " declares a forbidden evaluated package reference: Npgsql")
            else
                Some(source + " declares an unapproved evaluated package reference"))

    projectFailures @ packageFailures
