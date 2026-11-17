module ClaimCore.Tests.Architecture.CommandApiTests

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let private compile directory name body =
    let path = Path.Combine(directory, name + ".fsx")

    let references =
        [
            typeof<PrincipalKey>.Assembly.Location
            typeof<CommandRequest>.Assembly.Location
        ]
        |> List.map (fun path -> "#r " + JsonSerializer.Serialize(path))
        |> String.concat "\n"

    File.WriteAllText(
        path,
        references
        + "\nopen ClaimCore.Domain\nopen ClaimCore.Application.Contracts\n"
        + body
        + "\n"
    )

    let start =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for value in [ "fsi"; "--nologo"; "--warnaserror+"; "--exec"; path ] do
        start.ArgumentList.Add value

    let result = ClaimCore.TestSupport.BoundedProcess.run start None 32768 30000
    result.ExitCode, Encoding.UTF8.GetString result.StandardError

[<Tests>]
let tests =
    testCase
        "closed commands reject missing handlers and missing or mistyped bindings at compilation"
        (fun () ->
            let directory = Directory.CreateTempSubdirectory "claimcore-command-boundary-"

            try
                let code, error =
                    compile
                        directory.FullName
                        "complete"
                        "let control (principal:PrincipalKey) = CommandRequest.DismissAccessRequest principal\nlet handler = function CommandTaskId.DismissAccessRequest -> () | CommandTaskId.TerminateOwnSessions -> () | CommandTaskId.TerminatePersonSessions -> ()"

                Expect.equal code 0 ("Valid complete control and handler: " + error)

                let code, error =
                    compile
                        directory.FullName
                        "missing-handler"
                        "let handler = function CommandTaskId.DismissAccessRequest -> () | CommandTaskId.TerminateOwnSessions -> ()"

                Expect.isTrue
                    (code <> 0 && error.Contains("FS0025", StringComparison.Ordinal))
                    "An unhandled added operational command is a compiler error"

                let code, error =
                    compile
                        directory.FullName
                        "missing-principal"
                        "let control = CommandRequest.DismissAccessRequest ()"

                Expect.isTrue
                    (code <> 0 && error.Contains("FS0001", StringComparison.Ordinal))
                    "Missing principal cannot produce a complete command control"

                let code, error =
                    compile
                        directory.FullName
                        "wrong-binding"
                        "let control (principal:PrincipalKey) = CommandRequest.TerminatePersonSessions principal"

                Expect.isTrue
                    (code <> 0 && error.Contains("FS0001", StringComparison.Ordinal))
                    "Principal and registered person bindings cannot be interchanged"
            finally
                directory.Delete true)
