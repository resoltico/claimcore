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

/// Exact direct edges permitted by the product boundaries; docs link here.
let allowed =
    [
        "ClaimCore.Json", []
        "ClaimCore.Domain", [ "ClaimCore.Json" ]
        "ClaimCore.Contract", [ "ClaimCore.Domain"; "ClaimCore.Json" ]
        "ClaimCore.RecordFormat", [ "ClaimCore.Domain"; "ClaimCore.Json" ]
        "ClaimCore.Application",
        [
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Contract"
            "ClaimCore.Json"
        ]
        "ClaimCore.Postgres",
        [
            "ClaimCore.Application"
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Contract"
            "ClaimCore.Json"
        ]
        "ClaimCore.HostSecurity", []
        "ClaimCore.Web",
        [
            "ClaimCore.Application"
            "ClaimCore.Postgres"
            "ClaimCore.Contract"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
        "ClaimCore.Cli",
        [
            "ClaimCore.Contract"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
        "ClaimCore.Database",
        [
            "ClaimCore.Application"
            "ClaimCore.Postgres"
            "ClaimCore.Contract"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
    ]
    |> List.map (fun (name, edges) -> name, Set.ofList edges)
    |> Map.ofList

let permitted project references =
    Map.tryFind project allowed = Some references
