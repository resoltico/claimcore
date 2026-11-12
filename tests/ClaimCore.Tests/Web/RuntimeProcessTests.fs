module ClaimCore.Tests.RuntimeProcessTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Wire
open ClaimCore.Web

let private invoke arguments =
    task {
        let assembly = Assembly.GetExecutingAssembly().Location

        let config =
            ProcessStartInfo(
                "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        for argument in
            [
                "exec"
                "--runtimeconfig"
                (Path.ChangeExtension(assembly, ".runtimeconfig.json") |> nonNull)
                "--depsfile"
                (Path.ChangeExtension(assembly, ".deps.json") |> nonNull)
                typeof<WebStartupProblem>.Assembly.Location
            ]
            @ arguments do
            config.ArgumentList.Add argument

        for key in
            config.Environment.Keys
            |> Seq.filter (fun key -> key.StartsWith("CLAIMCORE_", StringComparison.Ordinal))
            |> Seq.toList do
            config.Environment.Remove key |> ignore

        config.Environment["CLAIMCORE_CONNECTION_FILE"] <-
            "/private/missing-configuration-free-witness"

        use child =
            Process.Start(config)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Host process did not start")

        try
            let output = child.StandardOutput.ReadToEndAsync()
            let errors = child.StandardError.ReadToEndAsync()
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 20.)
            let! output = output
            let! errors = errors
            return child.ExitCode, output, errors
        finally
            if not child.HasExited then
                child.Kill(true)
    }

[<Tests>]
let tests =
    testList
        "host metadata and typed diagnostics"
        [
            testCaseAsync
                "actual host discovery remains configuration-free after runtime wrapper removal"
                (async {
                    let! version, output, errors = invoke [ "version"; "--json" ] |> Async.AwaitTask
                    Expect.equal version 0 "Version opens no configured runtime"
                    Expect.equal errors "" "No startup/private input diagnostics"
                    use json = JsonDocument.Parse output

                    Expect.equal
                        (json.RootElement.GetProperty("version").GetString())
                        BuildIdentity.current.Version
                        "Compiled authoritative version"

                    let! help, output, errors = invoke [ "help" ] |> Async.AwaitTask
                    Expect.equal help 0 "Help opens no configured runtime"
                    Expect.equal errors "" "Help needs no provider"

                    Expect.stringContains
                        output
                        "configuration-free help"
                        "Supported invocation is documented"

                    Expect.isFalse
                        (output.Contains "missing-configuration-free-witness")
                        "Private configuration values are not read or displayed"
                })
            testCase "runtime open faults remain typed and encode bounded operator codes" (fun () ->
                let expected =
                    [
                        RuntimeOpenFault.InvalidTransport, "RUNTIME_INVALID_TRANSPORT"
                        RuntimeOpenFault.UnsupportedServer, "RUNTIME_UNSUPPORTED_SERVER"
                        RuntimeOpenFault.UnsafeRole, "RUNTIME_UNSAFE_ROLE"
                        RuntimeOpenFault.SchemaMismatch, "RUNTIME_SCHEMA_MISMATCH"
                        RuntimeOpenFault.InstallationInvalid, "RUNTIME_INSTALLATION_INVALID"
                        RuntimeOpenFault.HeadMismatch, "RUNTIME_HEAD_MISMATCH"
                        RuntimeOpenFault.Unavailable, "RUNTIME_UNAVAILABLE"
                        RuntimeOpenFault.Cancelled, "RUNTIME_CANCELLED"
                    ]

                for fault, code in expected do
                    use json =
                        JsonDocument.Parse(
                            WebStartupDiagnostics.encode (WebStartupProblem.RuntimeOpen fault)
                        )

                    Expect.equal
                        (json.RootElement.GetProperty("code").GetString())
                        code
                        "No string conversion or string dispatch at the boundary"

                    Expect.equal
                        (json.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                        (set [ "kind"; "code"; "setting" ])
                        "No provider exception or input fields")
        ]
