namespace ClaimCore.Docs

open System
open System.IO
open System.Security
open System.Text

[<RequireQualifiedAccess>]
module internal ExecutableHelp =
    let private helpProjects =
        [
            "ClaimCore.Cli", "src/ClaimCore.Cli/ClaimCore.Cli.fsproj"
            "ClaimCore.Database", "src/ClaimCore.Database/ClaimCore.Database.fsproj"
            "ClaimCore.Web", "src/ClaimCore.Web/ClaimCore.Web.fsproj"
        ]

    let private invoke context arguments timeout =
        context.Processes.Run(
            {
                FileName =
                    Environment.GetEnvironmentVariable("CLAIMCORE_DOTNET")
                    |> Option.ofObj
                    |> Option.defaultValue "dotnet"
                Arguments = arguments
                WorkingDirectory = context.Root.Path
                Environment =
                    [
                        "DOTNET_NOLOGO", Some "1"
                        "DOTNET_CLI_TELEMETRY_OPTOUT", Some "1"
                        "NO_COLOR", Some "1"
                        "TERM", Some "dumb"
                        "TZ", Some "UTC"
                        "CLAIMCORE_CONNECTION_FILE", None
                        "CLAIMCORE_ADMIN_CONNECTION_FILE", None
                        "CLAIMCORE_WEB_STATE_DIR", None
                        "CLAIMCORE_WEB_CERTIFICATE_PATH", None
                        "CLAIMCORE_WEB_ORIGIN", None
                        "CLAIMCORE_WEB_MAX_JSON_BYTES", None
                        "CLAIMCORE_WEB_CORE_PERMITS", None
                        "CLAIMCORE_WEB_CORE_QUEUE", None
                        "CLAIMCORE_WEB_LOGIN_PERMITS", None
                        "CLAIMCORE_WEB_SESSION_IDLE_MINUTES", None
                        "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES", None
                    ]
                Timeout = timeout
            }
        )
        |> Result.mapError (fun message -> [ Diagnostic.create DiagnosticCode.Invocation message ])

    let private requireSuccess purpose result =
        match result with
        | Error errors -> Error errors
        | Ok output when output.ExitCode <> 0 ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        $"{purpose} failed with exit code {output.ExitCode}."
                ]
        | Ok output -> Ok output

    let private validatedTarget context product (output: ProcessOutput) =
        let target = output.StandardOutput.Trim()

        match Repository.ensureExistingSafe context.Root target with
        | Error message -> Error [ Diagnostic.create DiagnosticCode.UnsafePath message ]
        | Ok safeTarget ->
            let relative = Repository.relativePath context.Root safeTarget
            let prefix = "artifacts/bin/" + product + "/"
            let suffix = "/" + product + ".dll"

            if
                not (relative.StartsWith(prefix, StringComparison.Ordinal))
                || not (relative.EndsWith(suffix, StringComparison.Ordinal))
            then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.UnsafePath
                            $"The resolved {product} artifact is outside its registered artifact tree."
                    ]
            else
                Ok safeTarget

    let private safeHelpProjects context =
        let projects =
            helpProjects
            |> List.map (fun (_, project) ->
                Repository.registeredPath context.Root project
                |> Result.bind (Repository.ensureExistingSafe context.Root))

        match
            projects
            |> List.tryPick (function
                | Error error -> Some error
                | Ok _ -> None)
        with
        | Some error -> Error [ Diagnostic.create DiagnosticCode.UnsafePath error ]
        | None ->
            Ok(
                projects
                |> List.choose (function
                    | Ok path -> Some path
                    | Error _ -> None)
            )

    let private helpSolution paths =
        let entries =
            paths
            |> List.map (fun path -> "  <Project Path=\"" + SecurityElement.Escape(path) + "\" />")

        String.concat "\n" ([ "<Solution>" ] @ entries @ [ "</Solution>"; "" ])

    let private buildHelpSolution context projects =
        let directory = Directory.CreateTempSubdirectory("claimcore-docs-help-")
        let solution = Path.Combine(directory.FullName, "requirements.slnx")

        try
            File.WriteAllText(solution, helpSolution projects, UTF8Encoding(false))

            let result =
                invoke
                    context
                    [
                        "build"
                        solution
                        "--configuration"
                        "Release"
                        "--no-restore"
                        "--disable-build-servers"
                        "-maxcpucount:1"
                        "-p:ShouldUnsetParentConfigurationAndPlatform=false"
                    ]
                    (TimeSpan.FromMinutes(float helpProjects.Length * 5.0))
                |> requireSuccess "Executable help build"
                |> Result.map ignore

            match result with
            | Ok() ->
                directory.Delete(true)
                Ok()
            | Error errors ->
                Error(
                    errors
                    @ [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            ("Failed help build requirements retained at " + solution + ".")
                    ]
                )
        with error ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        ("Help build requirements failed ("
                         + error.GetType().Name
                         + "); retained at "
                         + solution
                         + ".")
                ]

    let build context =
        safeHelpProjects context |> Result.bind (buildHelpSolution context)

    let private resolveExecutable context product =
        let project = helpProjects |> List.find (fun (name, _) -> name = product) |> snd

        invoke
            context
            [
                "msbuild"
                project
                "-nologo"
                "-verbosity:quiet"
                "-property:Configuration=Release"
                "-getProperty:TargetPath"
            ]
            (TimeSpan.FromMinutes(1.0))
        |> requireSuccess (product + " target resolution")
        |> Result.bind (validatedTarget context product)

    let private normalizedHelp output =
        if not (String.IsNullOrEmpty(output.StandardError)) then
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        "Executable help wrote to standard error."
                ]
        else
            let normalized = output.StandardOutput.Replace("\r\n", "\n")

            if normalized.Contains('\r') || normalized.Contains(char 0) then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            "Executable help contained unsupported control/newline data."
                    ]
            elif normalized.Contains("<!-- generated:", StringComparison.Ordinal) then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            "Executable help may not emit documentation marker text."
                    ]
            else
                let content = normalized.TrimEnd('\n') + "\n"
                Ok("```text\n" + content + "```\n")

    let render product context =
        resolveExecutable context product
        |> Result.bind (fun target ->
            invoke context [ target; "help" ] (TimeSpan.FromMinutes(1.0))
            |> requireSuccess (product + " help"))
        |> Result.bind normalizedHelp
