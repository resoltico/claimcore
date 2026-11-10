module internal ClaimCore.Tests.Architecture.ProjectReferences

open System.Diagnostics
open System.IO
open System.Text.Json

/// Evaluate imports and conditions without maintaining a second project graph.
let read path =
    let start =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in [ "msbuild"; path; "-nologo"; "-verbosity:quiet"; "-getItem:ProjectReference" ] do
        start.ArgumentList.Add argument

    use child = Process.Start start |> nonNull
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        invalidOp "Project reference evaluation timed out."

    if child.ExitCode <> 0 then
        invalidOp ("Project reference evaluation failed: " + errors.Result)

    use document = JsonDocument.Parse output.Result

    document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
    |> Seq.map (fun item ->
        item.GetProperty("Identity").GetString()
        |> nonNull
        |> Path.GetFileNameWithoutExtension
        |> nonNull)
    |> Set.ofSeq

/// Allowed direct edges; removing an unnecessary edge preserves the boundary.
let allowed =
    [
        "ClaimCore.Json", []
        "ClaimCore.Domain", []
        "ClaimCore.Application.Contracts", [ "ClaimCore.Domain" ]
        "ClaimCore.Presentation",
        [ "ClaimCore.Domain"; "ClaimCore.Application.Contracts"; "ClaimCore.Json" ]
        "ClaimCore.Wire",
        [
            "ClaimCore.RecordFormat"
            "ClaimCore.Domain"
            "ClaimCore.Application.Contracts"
            "ClaimCore.Json"
            "ClaimCore.Presentation"
        ]
        "ClaimCore.RecordFormat", [ "ClaimCore.Domain"; "ClaimCore.Json" ]
        "ClaimCore.Application",
        [
            "ClaimCore.Domain"
            "ClaimCore.Application.Contracts"
            "ClaimCore.RecordFormat"
        ]
        "ClaimCore.Postgres",
        [
            "ClaimCore.Application.Contracts"
            "ClaimCore.Application"
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Json"
        ]
        "ClaimCore.HostSecurity", []
        "ClaimCore.Web",
        [
            "ClaimCore.Application.Contracts"
            "ClaimCore.Application"
            "ClaimCore.Postgres"
            "ClaimCore.Wire"
            "ClaimCore.Presentation"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
        "ClaimCore.Cli",
        [
            "ClaimCore.Application.Contracts"
            "ClaimCore.Wire"
            "ClaimCore.Presentation"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
        "ClaimCore.Database",
        [
            "ClaimCore.RecordFormat"
            "ClaimCore.Application.Contracts"
            "ClaimCore.Application"
            "ClaimCore.Postgres"
            "ClaimCore.Wire"
            "ClaimCore.Presentation"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
    ]
    |> List.map (fun (name, edges) -> name, Set.ofList edges)
    |> Map.ofList

let permitted project references =
    match Map.tryFind project allowed with
    | Some permitted -> Set.isSubset references permitted
    | None -> false
