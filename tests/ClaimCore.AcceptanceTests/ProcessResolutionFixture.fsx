#r "ClaimCore.AcceptanceTests.dll"

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open ClaimCore.TestSupport

type Result =
    | Refused
    | Completed of int * string * string

let helper = Path.Combine(__SOURCE_DIRECTORY__, "claimcore-test-supervisor")
let mutable checks = 0

let require marker condition =
    if not condition then
        failwith ("RESOLUTION_" + marker)

let direct (start: ProcessStartInfo) =
    use child = new Process(StartInfo = start)

    try
        if not (child.Start()) then
            Refused
        else
            try
                let output = child.StandardOutput.ReadToEndAsync()
                let errors = child.StandardError.ReadToEndAsync()
                require "DIRECT_DEADLINE" (child.WaitForExit(2000))

                Completed(
                    child.ExitCode,
                    output.GetAwaiter().GetResult(),
                    errors.GetAwaiter().GetResult()
                )
            finally
                if not child.HasExited then
                    child.Kill()

                require "DIRECT_CLEANUP" (child.WaitForExit(2000))
    with :? Win32Exception ->
        Refused

let supervised (start: ProcessStartInfo) =
    try
        use owned = new ProcessSupervision.Session(helper, start)
        let output = owned.Process.StandardOutput.ReadToEndAsync()
        let errors = owned.Process.StandardError.ReadToEndAsync()

        let result =
            try
                let code = owned.Target.WaitAsync(TimeSpan.FromSeconds(2.)).GetAwaiter().GetResult()
                Completed(code, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult())
            with :? InvalidOperationException as error when
                error.Message = "Test process target could not start." ->
                Refused

        require "SUPERVISED_CLEANUP" (owned.Stop())
        result
    with :? ProcessStartupFailure as error when not error.CleanupFailed ->
        Refused

let start name =
    ProcessStartInfo(
        name,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    )

let compare marker expected configuration =
    let reference = direct (configuration ())
    require (marker + "_REFERENCE") (reference = expected)
    require (marker + "_PARITY") (supervised (configuration ()) = reference)
    checks <- checks + 1

let executable path marker enabled =
    File.WriteAllText(path, "#!/bin/sh\nprintf '%s' '" + marker + "'\n")
    let mode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    File.SetUnixFileMode(path, if enabled then mode ||| UnixFileMode.UserExecute else mode)

let identity () =
    let value = start "/usr/bin/id"
    value.ArgumentList.Add "-u"

    match direct value with
    | Completed(0, output, "") -> Int32.Parse(output.Trim())
    | _ -> failwith "RESOLUTION_IDENTITY"

let named name childPath () =
    let value = start name
    value.Environment["PATH"] <- childPath
    value

let searchCases directory cwd first second name =
    let a = Path.Combine(first, name)
    let b = Path.Combine(second, name)
    executable a "A" false
    executable b "B" true
    Environment.SetEnvironmentVariable("PATH", first + ":" + second)
    compare "SKIP_NONEXEC" (Completed(0, "B", "")) (named name "")
    executable a "A" true
    Environment.SetEnvironmentVariable("PATH", second)
    compare "PARENT_PATH" (Completed(0, "B", "")) (named name first)
    let shadow = Path.Combine(cwd, name)
    executable shadow "SHADOW" false
    compare "CWD_SHADOW" Refused (named name first)
    File.Delete shadow
    compare "ROOTED" (Completed(0, "B", "")) (named b "")
    compare "ROOTED_ABSENT" Refused (named (Path.Combine(directory, "absent")) "")
    compare "RELATIVE_NAME" (Completed(0, "B", "")) (named (Path.Combine("..", "second", name)) "")

let relativeCases directory second name =
    Environment.SetEnvironmentVariable("PATH", ":../second::")
    compare "RELATIVE_PATH" (Completed(0, "B", "")) (named name "")

    let different =
        Directory.CreateDirectory(Path.Combine(directory, "other", "deep")).FullName

    compare "RELATIVE_CHILD_CWD" Refused (fun () ->
        let value = start name
        value.WorkingDirectory <- different
        value)

    compare "ROOTED_CHILD_CWD" (Completed(0, "B", "")) (fun () ->
        let value = start (Path.Combine(second, name))
        value.WorkingDirectory <- different
        value)

let modeCases first second name root =
    let a = Path.Combine(first, name)

    File.SetUnixFileMode(
        a,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.OtherExecute
    )

    Environment.SetEnvironmentVariable("PATH", first + ":" + second)
    compare "OWNER_MODE" (Completed(0, (if root then "A" else "B"), "")) (named name "")
    File.Delete a
    let blocked = Path.Combine(first, "blocked")
    executable blocked "BLOCKED" false
    File.CreateSymbolicLink(a, blocked) |> ignore
    compare "FOLLOW_LINK" (Completed(0, "B", "")) (named name "")
    File.Delete a
    Directory.CreateDirectory(a) |> ignore
    compare "SKIP_DIRECTORY" (Completed(0, "B", "")) (named name "")
    Directory.Delete a
    let fifo = start "/usr/bin/mkfifo"
    fifo.ArgumentList.Add a
    require "FIFO_CREATED" (direct fifo = Completed(0, "", ""))

    File.SetUnixFileMode(
        a,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    compare "NONREGULAR_CANDIDATE" Refused (named name "")
    File.Delete a

let controllerCases originalPath =
    Environment.SetEnvironmentVariable("PATH", originalPath)

    compare "ARGV_ZERO" (Completed(0, "sh", "")) (fun () ->
        let value = start "sh"
        value.ArgumentList.Add "-c"
        value.ArgumentList.Add "printf '%s' \"$0\""
        value)

    Environment.SetEnvironmentVariable("PATH", "")

    let controller =
        Environment.ProcessPath
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "RESOLUTION_PROCESS_PATH")

    let configure () =
        let value = start (Path.GetFileName controller)
        value.ArgumentList.Add "--version"
        value

    let reference = direct (configure ())

    match reference with
    | Completed(0, output, "") when not (String.IsNullOrWhiteSpace output) -> ()
    | _ -> failwith "RESOLUTION_EXE_DIRECTORY_REFERENCE"

    require "EXE_DIRECTORY_PARITY" (supervised (configure ()) = reference)
    checks <- checks + 1

let directory =
    Directory.CreateTempSubdirectory("claimcore-resolver-fixture-").FullName

let previousDirectory = Directory.GetCurrentDirectory()
let previousPath = Environment.GetEnvironmentVariable "PATH"

try
    let cwd = Directory.CreateDirectory(Path.Combine(directory, "cwd")).FullName
    let first = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName
    let second = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName
    let name = "claimcore-synthetic-tool-" + Guid.NewGuid().ToString("N")
    let root = identity () = 0
    Directory.SetCurrentDirectory cwd
    searchCases directory cwd first second name
    relativeCases directory second name
    modeCases first second name root
    controllerCases previousPath
    printfn "RESOLUTION_PARITY_OK:%d" checks
finally
    Directory.SetCurrentDirectory previousDirectory
    Environment.SetEnvironmentVariable("PATH", previousPath)
    Directory.Delete(directory, true)
