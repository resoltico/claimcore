module ClaimCore.Tests.CliTrustRootProcessTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

let private frame =
    Encoding.UTF8.GetBytes
        "{\"protocolVersion\":4,\"endpoint\":\"case.list\",\"input\":{\"limit\":1}}"

let private invoke root path origin =
    let assembly =
        Path.Combine(root, "artifacts/bin/ClaimCore.Cli/release/ClaimCore.Cli.dll")

    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    CliProcessEnvironment.clearInherited start
    start.ArgumentList.Add assembly
    start.ArgumentList.Add "call"

    for key, value in
        [
            "CLAIMCORE_SERVICE_URL", origin
            "CLAIMCORE_OIDC_ISSUER", origin
            "CLAIMCORE_OIDC_CLIENT_ID", "synthetic-cli"
            "CLAIMCORE_CLI_AUTH_MODE", "interactive"
            "CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE", path
        ] do
        start.Environment[key] <- value

    BoundedProcess.run start (Some frame) 16384 30_000

let private privateDirectory () =
    let parent =
        if OperatingSystem.IsMacOS() then
            "/Users/Shared"
        else
            Path.GetTempPath()

    let path =
        Path.Combine(parent, "claimcore-cli-roots-" + Guid.NewGuid().ToString("N"))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory path |> ignore
    else
        Directory.CreateDirectory(
            path,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    path

let private refusedRoots () =
    let directory = privateDirectory ()
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()

    try
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let origin = $"https://localhost:{port}/"
        let malformed = Path.Combine(directory, "PRIVATE-ROOT-PATH-SENTINEL.pem")
        File.WriteAllText(malformed, "synthetic noncertificate", UTF8Encoding false)

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(malformed, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        let invalidCode =
            if OperatingSystem.IsWindows() then
                "CLI_PRIVATE_SOURCE_INVALID"
            else
                "CLI_CONFIGURATION_INVALID"

        for path, expected in
            [
                Path.Combine(directory, "PRIVATE-ROOT-PATH-SENTINEL-missing.pem"),
                "CLI_PRIVATE_SOURCE_INVALID"
                malformed, invalidCode
            ] do
            let result = invoke (RepositoryRoot.find ()) path origin
            Expect.equal result.ExitCode 3 "Local refusal before authentication"
            use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)
            let root = document.RootElement
            Expect.equal (root.GetProperty("kind").GetString()) "localFailure" "Transport family"
            Expect.equal (root.GetProperty("code").GetString()) expected "Bounded cause"

            Expect.equal
                (root.GetProperty("executionPhase").GetString())
                "NOT_STARTED"
                "No dispatch"

            let output =
                Encoding.UTF8.GetString(Array.append result.StandardOutput result.StandardError)

            Expect.isFalse
                (output.Contains("PRIVATE-ROOT-PATH-SENTINEL", StringComparison.Ordinal))
                "No path reflection"

            Expect.equal result.StandardError.Length 0 "No unstructured diagnostic"

            Expect.isFalse
                (listener.Pending())
                "Neither authentication nor service opened a connection"
    finally
        listener.Stop()
        Directory.Delete(directory, true)

let tests =
    testCase
        "[CC-CLI-002] native frames classify refused and malformed public roots without dispatch or path disclosure"
        refusedRoots
