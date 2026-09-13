namespace ClaimCore.Docs

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module Provenance =
    let private run (root: RepositoryRoot) (runner: IProcessRunner) executable arguments =
        runner.Run(
            {
                FileName = executable
                Arguments = arguments
                WorkingDirectory = root.Path
                Environment =
                    [
                        "DOTNET_NOLOGO", Some "1"
                        "DOTNET_CLI_TELEMETRY_OPTOUT", Some "1"
                        "NO_COLOR", Some "1"
                        "GIT_ALTERNATE_OBJECT_DIRECTORIES", None
                        "GIT_CEILING_DIRECTORIES", None
                        "GIT_COMMON_DIR", None
                        "GIT_CONFIG_COUNT", None
                        "GIT_CONFIG_PARAMETERS", None
                        "GIT_DIR", None
                        "GIT_DISCOVERY_ACROSS_FILESYSTEM", None
                        "GIT_INDEX_FILE", None
                        "GIT_NAMESPACE", None
                        "GIT_OBJECT_DIRECTORY", None
                        "GIT_OPTIONAL_LOCKS", Some "0"
                        "GIT_TERMINAL_PROMPT", Some "0"
                        "GIT_WORK_TREE", None
                    ]
                Timeout = TimeSpan.FromMinutes(1.0)
            }
        )

    let private identity root revision state files =
        let content = files |> RepositoryInventory.aggregateHash root

        let locks =
            RepositoryInventory.lockFiles root files
            |> RepositoryInventory.aggregateHash root

        {
            GitRevision = revision
            State = state
            ContentSha256 = content
            LocksSha256 = locks
        }

    let private gitMarkerExists (root: RepositoryRoot) =
        let marker = Path.Combine(root.Path, ".git")
        Directory.Exists(marker) || File.Exists(marker)

    let private gitWorktree root runner =
        match run root runner "git" [ "rev-parse"; "--is-inside-work-tree" ] with
        | Error _ when not (gitMarkerExists root) -> Ok false
        | Ok output when output.ExitCode <> 0 && not (gitMarkerExists root) -> Ok false
        | Ok output when output.ExitCode = 0 && output.StandardOutput.Trim() = "true" -> Ok true
        | _ -> Error "Git worktree state could not be established."

    let private gitOutput root runner purpose arguments =
        match run root runner "git" arguments with
        | Error _ -> Error $"{purpose} could not be established."
        | Ok output when output.ExitCode <> 0 -> Error $"{purpose} could not be established."
        | Ok output -> Ok output.StandardOutput

    let private gitRevision root runner =
        match run root runner "git" [ "rev-parse"; "--verify"; "HEAD^{commit}" ] with
        | Error _ -> Error "Git revision could not be established."
        | Ok output when output.ExitCode <> 0 ->
            match run root runner "git" [ "symbolic-ref"; "--quiet"; "HEAD" ] with
            | Ok symbolic when
                symbolic.ExitCode = 0
                && symbolic.StandardOutput
                    .Trim()
                    .StartsWith("refs/heads/", StringComparison.Ordinal)
                ->
                Ok None
            | _ -> Error "Git revision could not be established."
        | Ok output ->
            let revision = output.StandardOutput.Trim()

            if Regex.IsMatch(revision, "^[0-9a-f]{40}(?:[0-9a-f]{24})?$") then
                Ok(Some revision)
            else
                Error "Git revision has an invalid shape."

    let private gitIdentity root runner =
        gitOutput
            root
            runner
            "Git worktree status"
            [ "status"; "--porcelain=v1"; "--untracked-files=all" ]
        |> Result.bind (fun status ->
            gitOutput
                root
                runner
                "Git source inventory"
                [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z"; "--"; "." ]
            |> Result.bind (RepositoryInventory.gitSourceFiles root)
            |> Result.bind (fun files ->
                gitRevision root runner
                |> Result.map (fun revision ->
                    let state =
                        match revision with
                        | None -> "unborn"
                        | Some _ when String.IsNullOrEmpty(status) -> "clean"
                        | Some _ -> "dirty"

                    identity root revision state files)))

    let sourceIdentity root runner =
        try
            gitWorktree root runner
            |> Result.bind (fun isWorktree ->
                if isWorktree then
                    gitIdentity root runner
                else
                    let files = RepositoryInventory.sourceFiles root
                    Ok(identity root None "unversioned" files))
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let private optionalVersion root runner executable arguments trimPrefix =
        match run root runner executable arguments with
        | Ok result when result.ExitCode = 0 && String.IsNullOrWhiteSpace(result.StandardError) ->
            let value = result.StandardOutput.Trim()

            if value.StartsWith(trimPrefix, StringComparison.Ordinal) then
                Some(value.Substring(trimPrefix.Length))
            else
                Some value
        | _ -> None

    let toolchain root runner =
        match run root runner "dotnet" [ "--version" ] with
        | Error message -> Error message
        | Ok result when result.ExitCode <> 0 -> Error "The .NET SDK identity command failed."
        | Ok result ->
            try
                use postgres =
                    JsonDocument.Parse(
                        File.ReadAllBytes(Path.Combine(root.Path, "db/postgresql-baseline.json"))
                    )

                let image = postgres.RootElement.GetProperty("containerImage").GetString()

                Ok
                    {
                        DotnetSdk = result.StandardOutput.Trim()
                        Node = optionalVersion root runner "node" [ "--version" ] "v"
                        Npm = optionalVersion root runner "npm" [ "--version" ] ""
                        PostgreSql = Option.ofObj image
                        OperatingSystem = RuntimeInformation.OSDescription
                        Architecture = string RuntimeInformation.OSArchitecture
                    }
            with error ->
                Error(error.GetType().Name + ": " + error.Message)
