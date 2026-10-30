module ClaimCore.AcceptanceTests.PublishedCliProfileTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.TestSupport
open Expecto

let private privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private withCli action () =
    let directory = Directory.CreateTempSubdirectory("claimcore-cli-profile-")

    try
        let root = RepositoryRoot.find ()
        let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"
        action root directory.FullName cli
    finally
        directory.Delete true

let private configurationArguments =
    [
        "configure"
        "--service-url"
        "https://claims.example.test/"
        "--issuer"
        "https://identity.example.test/realm"
        "--client-id"
        "synthetic-cli"
        "--auth-mode"
        "interactive"
    ]

let private invoke cli environment arguments =
    ProcessRunner.dotnet 8000 cli arguments environment (Some [||])

let private profile directory cli =
    let environment = Dictionary<string, string>()
    let home = Directory.CreateDirectory(Path.Combine(directory, "home")).FullName
    let config = Directory.CreateDirectory(Path.Combine(directory, "config")).FullName
    environment["HOME"] <- home
    environment["XDG_CONFIG_HOME"] <- config
    let path = Path.Combine(config, "claimcore", "profile.json")
    let initial = invoke cli environment configurationArguments
    Expect.equal initial.ExitCode 0 "Published configure writes public settings"
    Expect.isEmpty initial.StandardOutput "Successful write has no token or secret output"
    Expect.isEmpty initial.StandardError "Successful write has no diagnostics"
    Expect.equal (File.GetUnixFileMode path) privateMode "Published writer creates 0600 profile"

    Expect.equal
        (File.GetUnixFileMode((Path.GetDirectoryName path |> nonNull)))
        (privateMode ||| UnixFileMode.UserExecute)
        "Published writer creates 0700 directory"

    let original = File.ReadAllBytes path

    let invalid =
        invoke cli environment [ "configure"; "--service-url"; "http://claims.example.test/" ]

    Expect.equal invalid.ExitCode 3 "Insecure URL is refused"
    Expect.sequenceEqual (File.ReadAllBytes path) original "Refused write preserves prior profile"
    let secret = Path.Combine(directory, "secret")
    File.WriteAllText(secret, "SECRET-CONTENT-MUST-NOT-APPEAR")
    File.SetUnixFileMode(secret, UnixFileMode.None)
    environment["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] <- secret
    environment["CLAIMCORE_SERVICE_URL"] <- "https://override.example.test/"
    let shown = invoke cli environment [ "configure"; "--show" ]
    Expect.equal shown.ExitCode 0 "Published show needs neither network nor secret-file read"
    let source = Encoding.UTF8.GetString shown.StandardOutput
    Expect.isFalse (source.Contains "SECRET-CONTENT-MUST-NOT-APPEAR") "No secret content in show"
    Expect.isFalse (source.Contains secret) "Secret-file setting shows presence instead of path"
    use document = JsonDocument.Parse(ReadOnlyMemory shown.StandardOutput)
    let settings = document.RootElement.GetProperty "settings"

    let setting (key: string) (field: string) =
        settings.GetProperty(key).GetProperty(field).GetString()

    Expect.equal (setting "CLAIMCORE_SERVICE_URL" "source") "environment" "Source follows override"

    Expect.equal
        (setting "CLAIMCORE_SERVICE_URL" "value")
        "https://override.example.test/"
        "Effective service override"

    Expect.equal
        (setting "CLAIMCORE_OIDC_ISSUER" "source")
        "profile"
        "Unchanged issuer comes from profile"

    for mode in
        [
            privateMode ||| UnixFileMode.GroupWrite
            privateMode ||| UnixFileMode.OtherWrite
        ] do
        File.SetUnixFileMode(path, mode)
        let result = invoke cli environment [ "configure"; "--show" ]
        Expect.equal result.ExitCode 3 "Writable profile is refused by published reader"
        Expect.isOk (Frames.decode result.StandardOutput) "Closed safe configuration failure"

    File.SetUnixFileMode(path, privateMode)
    File.WriteAllText(path, "{\"clientSecret\":\"FORBIDDEN-PROFILE-CONTENT\"}")
    environment["CLAIMCORE_OIDC_ISSUER"] <- "https://identity.example.test/realm"
    environment["CLAIMCORE_OIDC_CLIENT_ID"] <- "synthetic-cli"
    environment["CLAIMCORE_CLI_AUTH_MODE"] <- "interactive"
    let rejected = invoke cli environment [ "configure"; "--show" ]
    Expect.equal rejected.ExitCode 3 "Complete environment cannot bypass refused profile members"

    Expect.isFalse
        (Encoding.UTF8.GetString(rejected.StandardOutput).Contains "FORBIDDEN-PROFILE-CONTENT")
        "Refusal never echoes profile content"

    let syntax =
        invoke cli environment [ "configure"; "--client-secret"; "SECRET-ARGV-CANARY" ]

    Expect.equal syntax.ExitCode 64 "Secret option is not part of configure grammar"
    Expect.isEmpty syntax.StandardOutput "Secret option does not produce a saved configuration"

    Expect.isFalse
        (Encoding.UTF8.GetString(syntax.StandardError).Contains "SECRET-ARGV-CANARY")
        "Secret-looking option value is not echoed"

    Expect.isEmpty
        (Directory.GetFileSystemEntries home)
        "Configure writes only to its explicit XDG location"

let private terminal root directory cli =
    let home = Directory.CreateDirectory(Path.Combine(directory, "home")).FullName

    let start =
        ProcessStartInfo(
            "python3",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    start.ArgumentList.Add(
        Path.Combine(root, "tests/ClaimCore.AcceptanceTests/ShellTerminalProbe.py")
    )

    start.ArgumentList.Add cli
    start.ArgumentList.Add home
    start.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
    let result = BoundedProcess.run start None 1048576 60000

    if result.ExitCode <> 0 then
        // The probe uses synthetic public settings and no network; its traceback identifies the failed terminal step.
        Console.Error.WriteLine(Encoding.UTF8.GetString result.StandardError)

    Expect.equal result.ExitCode 0 "Real native terminal scenarios complete"
    use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)

    for name in
        [
            "history"
            "literalMetacharacters"
            "quoting"
            "interrupt"
            "exit"
            "eof"
            "sigterm"
            "automationRefused"
            "redirectedReviewRefused"
            "homeUnchanged"
        ] do
        Expect.isTrue
            (document.RootElement.GetProperty(name).GetBoolean())
            "Required terminal scenario passed"

    Expect.isEmpty
        (Directory.GetFileSystemEntries home)
        "Independent parent check finds no history or other files"

    let environment = Dictionary<string, string>()
    environment["HOME"] <- home
    let refused = invoke cli environment [ "shell" ]
    Expect.equal refused.ExitCode 3 "Non-terminal stdin is refused before sign-in"

    Expect.isOk
        (Frames.decode refused.StandardOutput)
        "Non-terminal refusal is a definite local frame"

[<Tests>]
let tests =
    testList
        "published CLI profile and shell"
        [
            testCase
                "profile permissions sources and secret refusals"
                (withCli (fun _ directory cli -> profile directory cli))
            testCase "native terminal history signals EOF and automation refusal" (withCli terminal)
        ]
    |> testSequenced
