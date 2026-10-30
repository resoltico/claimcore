module ClaimCore.Tests.ShellHomeTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Xml.Linq
open ClaimCore.Cli
open ClaimCore.TestSupport
open Expecto

let private scenarioName = "shell runtime leaves empty HOME unchanged"
let private childMarker = "CLAIMCORE_SHELL_EMPTY_HOME_CHILD"

let private inEmptyHome path =
    Expect.equal (Environment.GetEnvironmentVariable "HOME") path "Child runs with the empty HOME"
    Expect.isEmpty (Directory.GetFileSystemEntries path) "Positive empty-HOME precondition"

    let read name =
        if name = "HOME" then Some path else None

    Expect.isOk (RemoteConfiguration.sources read) "Absent default profile is a read-only operation"
    ShellSessionTests.runSignInAndExit ()

    Expect.isEmpty
        (Directory.GetFileSystemEntries path)
        "Authenticated commands, refresh material, exit and EOF write no files"

let private childPassed report =
    let document = XDocument.Load(report: string)

    let namespaceName =
        XNamespace.Get "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

    let counters = document.Descendants(namespaceName + "Counters") |> Seq.exactlyOne

    for name in [ "total"; "executed"; "passed" ] do
        Expect.equal
            ((counters.Attribute(XName.Get name) |> nonNull).Value)
            "1"
            "Exactly the selected child scenario executed"

    Expect.equal
        ((counters.Attribute(XName.Get "failed") |> nonNull).Value)
        "0"
        "Child scenario passed"

    Expect.equal
        ((counters.Attribute(XName.Get "notExecuted") |> nonNull).Value)
        "0"
        "Child cannot be skipped"

let private isolated () =
    let directory = Directory.CreateTempSubdirectory("claimcore-shell-home-")

    let home =
        Directory.CreateDirectory(Path.Combine(directory.FullName, "home")).FullName

    let report = Path.Combine(directory.FullName, "child.trx")
    let root = RepositoryRoot.find ()

    try
        let start =
            ProcessStartInfo(
                "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        start.WorkingDirectory <- root
        CliProcessEnvironment.clearInherited start

        for argument in
            [
                Assembly.GetExecutingAssembly().Location
                "--filter"
                "FullyQualifiedName~" + scenarioName
                "--report-trx"
                "--report-trx-filename"
                report
                "--settings=" + Path.Combine(root, "eng/expecto.runsettings")
            ] do
            start.ArgumentList.Add argument

        start.Environment["HOME"] <- home
        start.Environment.Remove "XDG_CONFIG_HOME" |> ignore
        start.Environment[childMarker] <- home
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
        start.Environment["DOTNET_NOLOGO"] <- "1"
        let result = BoundedProcess.run start None 1048576 30000
        Expect.equal result.ExitCode 0 "Isolated native shell scenario exits successfully"
        childPassed report

        Expect.isEmpty
            (Directory.GetFileSystemEntries home)
            "Independent parent check finds no new files"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testCase scenarioName (fun () ->
        match Environment.GetEnvironmentVariable childMarker |> Option.ofObj with
        | Some path -> inEmptyHome path
        | None -> isolated ())
