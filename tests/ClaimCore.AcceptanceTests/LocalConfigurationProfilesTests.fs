module ClaimCore.AcceptanceTests.LocalConfigurationProfilesTests

open System
open System.Diagnostics
open System.IO
open System.Collections.Generic
open System.Text
open System.Text.Json
open ClaimCore.TestSupport
open Expecto

let private privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
let private directoryMode = privateMode ||| UnixFileMode.UserExecute

let private executable name =
    (Environment.GetEnvironmentVariable "PATH" |> nonNull).Split(Path.PathSeparator)
    |> Array.map (fun directory -> Path.Combine(directory, name))
    |> Array.tryFind File.Exists

let private run executable arguments environment =
    let start =
        ProcessStartInfo(
            executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in arguments do
        start.ArgumentList.Add argument

    for KeyValue(key, value) in environment do
        start.Environment[key] <- value

    BoundedProcess.run start None 1048576 120000

let private withoutCertutil directory =
    let binaries = Directory.CreateDirectory(Path.Combine(directory, "tools")).FullName

    for name in
        [
            "openssl"
            "docker"
            "node"
            "mkdir"
            "cp"
            "cat"
            "rm"
            "id"
            "bash"
            "docker-credential-desktop"
            "docker-credential-osxkeychain"
            "docker-credential-secretservice"
        ] do
        match executable name with
        | Some target -> File.CreateSymbolicLink(Path.Combine(binaries, name), target) |> ignore
        | None when name.StartsWith("docker-credential-", StringComparison.Ordinal) -> ()
        | None -> failtest ("Required setup-test executable is missing: " + name)

    Map.ofList [ "PATH", binaries ]

let private configure root path environment =
    run
        "/bin/bash"
        [
            Path.Combine(root, "deployment/configure-local.sh")
            path
            "Etc/UTC"
            "7181"
            "7182"
        ]
        environment

let private restoreOwnership root configuration =
    if Directory.Exists configuration then
        let identifier flag =
            let result = run "id" [ flag ] Map.empty

            if result.ExitCode <> 0 then
                failtest "Cannot determine owned cleanup identity."

            Encoding.UTF8.GetString(result.StandardOutput).Trim() |> UInt32.Parse |> string

        use source =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "db/postgresql-baseline.json")))

        let targets = source.RootElement.GetProperty "supportedMajors"
        let image = targets[0].GetProperty("containerImage").GetString() |> nonNull

        let mount =
            "type=bind,\"source="
            + configuration.Replace("\"", "\"\"")
            + "\",target=/config"

        let owner = identifier "-u" + ":" + identifier "-g"

        let result =
            run
                "docker"
                [
                    "run"
                    "--rm"
                    "--network"
                    "none"
                    "--read-only"
                    "--mount"
                    mount
                    "--user"
                    "0"
                    "--entrypoint"
                    "/bin/sh"
                    image
                    "-c"
                    "chown -R \"$1\" /config"
                    "owned-cleanup"
                    owner
                ]
                Map.empty

        Expect.equal
            result.ExitCode
            0
            "Only this newly generated configuration regains its owner for cleanup"

let private withInputs missing action () =
    let owned = Directory.CreateTempSubdirectory("claimcore-local-profiles-")
    let root = RepositoryRoot.find ()
    let path = Path.Combine(owned.FullName, "configuration 'quoted $value' # path")

    let cleanup =
        [ (fun () -> restoreOwnership root path); (fun () -> owned.Delete true) ]

    try
        if executable "certutil" |> Option.isNone then
            failtest "Install NSS certutil to qualify actual isolated CA trust."

        let environment =
            if missing then
                withoutCertutil owned.FullName
            else
                Map.empty

        let result = configure root path environment

        Expect.equal
            result.ExitCode
            0
            ("Fresh local configuration succeeds: "
             + Encoding.UTF8.GetString result.StandardError)

        action root path result
    with error ->
        FixtureDisposal.afterFailure error cleanup

    FixtureDisposal.finish cleanup

let private trusted profile =
    let result = run "certutil" [ "-L"; "-d"; "sql:" + profile ] Map.empty
    Expect.equal result.ExitCode 0 "Real NSS database is readable"
    let lines = Encoding.UTF8.GetString(result.StandardOutput).Split('\n')

    Expect.isTrue
        (lines
         |> Array.exists (fun line ->
             line.StartsWith("ClaimCore evaluation CA", StringComparison.Ordinal)
             && line.TrimEnd().EndsWith("C,,", StringComparison.Ordinal)))
        "The actual isolated certificate database has SSL CA trust C,,"

let private generated _ configuration _ =
    Expect.isSome
        (executable "certutil")
        "Install NSS certutil to qualify isolated profile creation"

    let browser = Path.Combine(configuration, "browser-profile")
    Expect.equal (File.GetUnixFileMode browser) directoryMode "NSS profile directory is 0700"
    trusted browser
    let directory = Path.Combine(configuration, "cli", "claimcore")
    let profile = Path.Combine(directory, "profile.json")
    Expect.equal (File.GetUnixFileMode directory) directoryMode "CLI profile directory is 0700"
    Expect.equal (File.GetUnixFileMode profile) privateMode "CLI profile file is 0600"
    use document = JsonDocument.Parse(File.ReadAllBytes profile)
    let root = document.RootElement
    let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    Expect.equal
        names
        (set
            [
                "serviceUrl"
                "issuer"
                "clientId"
                "authMode"
                "serviceTrustRootFile"
                "oidcTrustRootFile"
            ])
        "Only the six public connection members are generated"

    let value (name: string) =
        root.GetProperty(name).GetString() |> nonNull

    Expect.equal (value "serviceUrl") "https://localhost:7181/" "Configured Web port"

    Expect.equal
        (value "issuer")
        "https://identity.localhost:7182/realms/claimcore"
        "Configured issuer port"

    Expect.equal (value "clientId") "claimcore-cli" "Public PKCE client"
    Expect.equal (value "authMode") "interactive" "Human setup remains interactive"

    for name in [ "serviceTrustRootFile"; "oidcTrustRootFile" ] do
        Expect.equal
            (value name)
            (Path.Combine(configuration, "ca.pem"))
            "Exact quoted public CA path"

let private missing _ configuration (result: BoundedProcessResult) =
    Expect.isFalse
        (Directory.Exists(Path.Combine(configuration, "browser-profile")))
        "Missing utility does not pretend to create trust"

    let commands =
        Encoding.UTF8.GetString(result.StandardOutput).Split('\n')
        |> Array.filter (fun line ->
            line.StartsWith("mkdir -m 700 ", StringComparison.Ordinal)
            || line.StartsWith("certutil -", StringComparison.Ordinal))

    Expect.equal commands.Length 4 "All exact recovery commands are printed"
    let script = Path.Combine(configuration, "create-browser-profile.sh")
    File.WriteAllText(script, "set -euo pipefail\n" + String.concat "\n" commands + "\n")
    File.SetUnixFileMode(script, privateMode)
    let applied = run "/bin/bash" [ script ] Map.empty

    Expect.equal
        applied.ExitCode
        0
        "Printed commands execute with the real utility and preserve the literal path"

    trusted (Path.Combine(configuration, "browser-profile"))

let private existing root configuration _ =
    let marker = Path.Combine(configuration, "retained-marker")
    File.WriteAllText(marker, "retained")

    let before =
        File.ReadAllBytes(Path.Combine(configuration, "cli", "claimcore", "profile.json"))

    let result = configure root configuration Map.empty
    Expect.equal result.ExitCode 2 "Existing configuration is refused"
    let linked = configuration + "-link"
    Directory.CreateSymbolicLink(linked, configuration) |> ignore
    Expect.equal (configure root linked Map.empty).ExitCode 2 "Existing directory links are refused"
    let file = configuration + "-file"
    File.WriteAllText(file, "retained file")
    Expect.equal (configure root file Map.empty).ExitCode 2 "Existing regular files are refused"
    Expect.equal (File.ReadAllText file) "retained file" "Existing regular-file data is preserved"
    Expect.equal (File.ReadAllText marker) "retained" "Existing data remains unchanged"

    Expect.sequenceEqual
        (File.ReadAllBytes(Path.Combine(configuration, "cli", "claimcore", "profile.json")))
        before
        "Existing profile is never replaced"

[<Tests>]
let tests =
    testList
        "local configuration profiles"
        [
            testCase
                "creates real isolated CA trust and private public-only CLI settings"
                (withInputs false generated)
            testCase
                "missing certutil prints executable exact recovery commands"
                (withInputs true missing)
            testCase
                "existing paths refuse without replacing profile or data"
                (withInputs false existing)
        ]
    |> testSequenced
