module internal ClaimCore.Tests.Architecture.ProjectReferences

open ClaimCore.CommandLine

open System.Diagnostics
open System.IO
open System.Text.Json

let private evaluate path arguments =
    let start =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in [ "msbuild"; path; "-nologo"; "-verbosity:quiet" ] @ arguments do
        start.ArgumentList.Add argument

    use child = Process.Start start |> nonNull
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        invalidOp "Project reference evaluation timed out."

    if child.ExitCode <> 0 then
        invalidOp ("Project reference evaluation failed: " + errors.Result)

    output.Result

let shippingConfigurations cli =
    let runtimes = (evaluate cli [ "-getProperty:RuntimeIdentifiers" ]).Trim()

    [
        "Debug", None
        "Release", None
        for runtime in runtimes.Split(';', System.StringSplitOptions.RemoveEmptyEntries) do
            "Release", Some runtime
    ]

/// Evaluate imports and conditions without maintaining a second project graph.
let read (configuration, runtime) path =
    let arguments =
        [
            $"-property:Configuration={configuration}"
            match runtime with
            | Some value ->
                $"-property:RuntimeIdentifier={value}"
                $"-property:ClaimCoreCliReleaseRid={value}"
            | None -> ()
            "-getItem:ProjectReference"
        ]

    use document = JsonDocument.Parse(evaluate path arguments)

    document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
    |> Seq.map (fun item -> item.GetProperty("FullPath").GetString() |> nonNull |> Path.GetFullPath)
    |> Set.ofSeq

/// Allowed direct edges; removing an unnecessary edge preserves the boundary.
let allowed =
    [
        "ClaimCore.CommandLine", [ "ClaimCore.Domain"; "ClaimCore.Json" ]
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
            "ClaimCore.Json"
        ]
        "ClaimCore.HostSecurity", []
        "ClaimCore.Server",
        [
            "ClaimCore.CommandLine"
            "ClaimCore.RecordFormat"
            "ClaimCore.Application.Contracts"
            "ClaimCore.Application"
            "ClaimCore.Wire"
            "ClaimCore.Presentation"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]
        "ClaimCore.Cli",
        [
            "ClaimCore.CommandLine"
            "ClaimCore.Application.Contracts"
            "ClaimCore.Wire"
            "ClaimCore.Presentation"
            "ClaimCore.Domain"
            "ClaimCore.HostSecurity"
            "ClaimCore.Json"
        ]

    ]
    |> List.map (fun (name, edges) -> name, Set.ofList edges)
    |> Map.ofList

let permitted (projects: Map<string, string>) project references =
    match Map.tryFind project allowed with
    | Some permitted -> Set.isSubset references (Set.map (fun name -> projects[name]) permitted)
    | None -> false
