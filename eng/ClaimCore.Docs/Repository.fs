namespace ClaimCore.Docs

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

[<Sealed>]
type RepositoryRoot internal (path: string) =
    member _.Path = path

    static member internal CreateForTests(path: string) =
        RepositoryRoot(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))

[<RequireQualifiedAccess>]
module Repository =
    let private comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private slash (value: string) =
        value.Replace(Path.DirectorySeparatorChar, '/')

    let internal isReparse (path: string) =
        File.GetAttributes(path) &&& FileAttributes.ReparsePoint <> enum 0

    let private ensureRootSafety (path: string) =
        if isReparse path then
            Error "The repository root must not be a symbolic link or junction."
        else
            Ok()

    let discover startDirectory =
        let rec candidates (current: string) (found: string list) =
            let solution = Path.Combine(current, "ClaimCore.slnx")
            let properties = Path.Combine(current, "Directory.Build.props")
            let next = Directory.GetParent(current)

            let updated =
                if File.Exists(solution) && File.Exists(properties) then
                    current :: found
                else
                    found

            match Option.ofObj next with
            | None -> updated
            | Some parent -> candidates parent.FullName updated

        let start = Path.GetFullPath(startDirectory)

        match candidates start [] with
        | [ root ] -> ensureRootSafety root |> Result.map (fun () -> RepositoryRoot(root))
        | [] -> Error "Could not locate a ClaimCore repository root."
        | _ -> Error "Nested ClaimCore repository roots are ambiguous."

    let private isContained (root: RepositoryRoot) (fullPath: string) =
        let prefix =
            root.Path.TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        String.Equals(fullPath, root.Path, comparison)
        || fullPath.StartsWith(prefix, comparison)

    let private exactChild (parent: string) (expected: string) =
        Directory.EnumerateFileSystemEntries(parent)
        |> Seq.tryFind (fun entry ->
            String.Equals(Path.GetFileName(entry), expected, StringComparison.Ordinal))

    let ensureExistingSafe (root: RepositoryRoot) fullPath =
        let candidate = Path.GetFullPath(fullPath)

        if not (isContained root candidate) then
            Error "The resolved path leaves the repository."
        else
            let relative = Path.GetRelativePath(root.Path, candidate)

            let parts =
                relative.Split(
                    [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                    StringSplitOptions.RemoveEmptyEntries
                )

            let mutable current = root.Path
            let mutable failure = None

            for part in parts do
                if failure.IsNone then
                    match exactChild current part with
                    | None ->
                        failure <-
                            Some $"Path component '{part}' is missing or has different casing."
                    | Some actual when isReparse actual ->
                        failure <- Some $"Path component '{part}' is a symbolic link or junction."
                    | Some actual -> current <- actual

            match failure with
            | Some message -> Error message
            | None -> Ok candidate

    let registeredPath (root: RepositoryRoot) relative =
        if
            String.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains(char 92)
            || relative.Contains(char 0)
        then
            Error "A registered path must be a nonempty repository-relative slash path."
        else
            let parts = relative.Split('/')

            if parts |> Array.exists (fun part -> part = "" || part = "." || part = "..") then
                Error "Registered paths must not contain empty, dot, or parent segments."
            else
                let fullPath = Path.GetFullPath(Path.Combine(root.Path, Path.Combine(parts)))

                if isContained root fullPath then
                    Ok fullPath
                else
                    Error "Registered path escaped the repository."

    let localPath (root: RepositoryRoot) baseDirectory relative =
        if
            String.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains(char 92)
            || relative.Contains(char 0)
        then
            Error "A local link must be a nonempty relative slash path."
        else
            let fullPath =
                relative.Split('/')
                |> Path.Combine
                |> fun path -> Path.GetFullPath(Path.Combine(baseDirectory, path))

            if not (isContained root fullPath) then
                Error "The local link leaves the repository."
            else
                ensureExistingSafe root fullPath

    let relativePath (root: RepositoryRoot) fullPath =
        Path.GetRelativePath(root.Path, fullPath) |> slash

    let sha256Bytes (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexStringLower

    let sha256Text (value: string) =
        value |> Encoding.UTF8.GetBytes |> sha256Bytes

    let sha256File path = File.ReadAllBytes(path) |> sha256Bytes

    /// Source admission owns membership and Git ignore rules, including isolated Gitless exports.
    let markdownFiles (root: RepositoryRoot) (runner: IProcessRunner) =
        let request =
            {
                FileName = "node"
                Arguments = [ "eng/ci/repository.mjs"; root.Path ]
                WorkingDirectory = root.Path
                Environment = []
                Timeout = TimeSpan.FromSeconds 60.0
            }

        match runner.Run request with
        | Error _ -> Error "The Markdown source inventory could not be admitted."
        | Ok output when output.ExitCode <> 0 || output.StandardError <> "" ->
            Error "The Markdown source inventory could not be admitted."
        | Ok output ->
            try
                let paths =
                    JsonSerializer.Deserialize<string array>(output.StandardOutput)
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> invalidOp "Missing source inventory")

                paths
                |> Array.distinct
                |> Array.filter (fun path -> path.EndsWith(".md", StringComparison.Ordinal))
                |> Array.sortWith (fun left right -> String.CompareOrdinal(left, right))
                |> Array.map (fun path ->
                    Path.Combine(root.Path, path.Replace('/', Path.DirectorySeparatorChar)))
                |> Array.filter File.Exists
                |> Array.toList
                |> Ok
            with _ ->
                Error "The Markdown source inventory was malformed."

module private ProcessDelivery =
    let private settle (child: Process) (readers: Task) =
        try
            if not child.HasExited then
                child.Kill(true)
                child.WaitForExit(2000) |> ignore
        with _ ->
            ()

        try
            readers.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
        with _ ->
            ()

    let collect (request: ProcessRequest) (child: Process) =
        use deadline = new System.Threading.CancellationTokenSource(request.Timeout)
        let capture = ConsoleCapture(16 * 1024 * 1024)
        let stdout = capture.Read(child.StandardOutput.BaseStream, deadline.Token)
        let stderr = capture.Read(child.StandardError.BaseStream, deadline.Token)
        let readers = Task.WhenAll([| stdout :> Task; stderr :> Task |])
        let exited = child.WaitForExitAsync()

        try
            if not (exited.Wait(request.Timeout)) then
                invalidOp "Deadline"

            readers.WaitAsync(request.Timeout).GetAwaiter().GetResult()

            if capture.Overflow then
                Error("Console capture overflow; child exit " + string child.ExitCode + ".")
            else
                Ok
                    {
                        ExitCode = child.ExitCode
                        StandardOutput = Encoding.UTF8.GetString(stdout.Result)
                        StandardError = Encoding.UTF8.GetString(stderr.Result)
                    }
        with error ->
            deadline.Cancel()
            settle child readers

            let status =
                if child.HasExited then
                    string child.ExitCode
                else
                    "unsettled"

            Error("Child delivery refused; exit " + status + "; " + error.GetType().Name + ".")

[<Sealed>]
type SystemProcessRunner() =
    interface IProcessRunner with
        member _.Run request =
            try
                let start = ProcessStartInfo(request.FileName)
                start.WorkingDirectory <- request.WorkingDirectory
                start.UseShellExecute <- false
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                request.Arguments |> List.iter start.ArgumentList.Add

                for key, value in request.Environment do
                    match value with
                    | Some content -> start.Environment[key] <- content
                    | None -> start.Environment.Remove(key) |> ignore

                use child = new Process(StartInfo = start)

                if child.Start() then
                    ProcessDelivery.collect request child
                else
                    Error "The child process did not start."
            with error ->
                Error("Child setup refused; " + error.GetType().Name + ".")
