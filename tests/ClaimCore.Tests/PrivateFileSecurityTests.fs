module ClaimCore.Tests.PrivateFileSecurityTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli

let private privateFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private privateDirectoryMode = privateFileMode ||| UnixFileMode.UserExecute

let private physicalTemp () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && temporary.StartsWith("/var/", StringComparison.Ordinal)
    then
        "/private" + temporary
    elif
        OperatingSystem.IsMacOS()
        && temporary.StartsWith("/tmp/", StringComparison.Ordinal)
    then
        "/private" + temporary
    else
        temporary

let private withSandbox action =
    let directory =
        Path.Combine(physicalTemp (), "claimcore-private-test-" + Guid.NewGuid().ToString("N"))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(directory, privateDirectoryMode) |> ignore

    try
        action directory
    finally
        Directory.Delete(directory, true)

let private makeFile directory name bytes =
    let path = Path.Combine(directory, name)
    let options = FileStreamOptions()
    options.Mode <- FileMode.CreateNew
    options.Access <- FileAccess.Write
    options.Share <- FileShare.None

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- privateFileMode

    use stream = new FileStream(path, options)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush(true)
    path

let private expectRefused message result =
    match result with
    | Error(error: string) ->
        Expect.isFalse (error.Contains("claimcore-private-test-", StringComparison.Ordinal)) message
    | Ok _ -> failtest message

let private windowsReadRefusal () =
    withSandbox (fun directory ->
        let path = makeFile directory "source.json" (Encoding.UTF8.GetBytes("synthetic"))

        PrivateFiles.readSource 64 path
        |> expectRefused "Windows private read fails closed")

let private windowsWriteRefusal () =
    withSandbox (fun directory ->
        let path = Path.Combine(directory, "export.json")

        PrivateFiles.writeNew path (Encoding.UTF8.GetBytes("synthetic"))
        |> expectRefused "Windows private export fails closed"

        Expect.isFalse (File.Exists(path)) "Unsupported export creates no file")

let private addBroadAcl path =
    let start = ProcessStartInfo("/bin/chmod")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add("+a")
    start.ArgumentList.Add("everyone allow read")
    start.ArgumentList.Add(path)
    use child = new Process(StartInfo = start)
    Expect.isTrue (child.Start()) "Synthetic ACL command starts"
    let output = child.StandardOutput.ReadToEndAsync()
    let diagnostics = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(5000)) then
        child.Kill(true)
        failtest "Synthetic ACL command timed out"

    output.GetAwaiter().GetResult() |> ignore
    diagnostics.GetAwaiter().GetResult() |> ignore
    Expect.equal child.ExitCode 0 "Synthetic extended ACL was installed"

let private privateReadBoundariesPosix () =
    withSandbox (fun directory ->
        let bytes = Encoding.UTF8.GetBytes("synthetic")
        let path = makeFile directory "source.json" bytes
        Expect.equal (PrivateFiles.readSource 9 path) (Ok bytes) "Private source is read exactly"
        PrivateFiles.readSource 8 path |> expectRefused "Exact source byte limit"
        File.SetUnixFileMode(path, privateFileMode ||| UnixFileMode.GroupRead)

        PrivateFiles.readSource 9 path
        |> expectRefused "Group-readable source is refused"

        File.SetUnixFileMode(path, privateFileMode)

        if OperatingSystem.IsMacOS() then
            addBroadAcl path

            Expect.equal
                (File.GetUnixFileMode(path))
                privateFileMode
                "ACL leaves Unix mode private"

            PrivateFiles.readSource 9 path |> expectRefused "Extended ACL source is refused"

        let invalid = makeFile directory "invalid.json" [| 0xFFuy |]
        PrivateFiles.readSource 1 invalid |> expectRefused "Invalid UTF-8 is refused"

        PrivateFiles.readSource 9 (directory + "//source.json")
        |> expectRefused "Noncanonical source path is refused"

        PrivateFiles.readSource 9 (path + "\u0000-suffix")
        |> expectRefused "Embedded NUL source path is refused")

let private linksAndNonFilesPosix () =
    withSandbox (fun directory ->
        let target = makeFile directory "target.json" (Encoding.UTF8.GetBytes("synthetic"))
        let link = Path.Combine(directory, "source-link.json")
        File.CreateSymbolicLink(link, target) |> ignore
        PrivateFiles.readSource 64 link |> expectRefused "Source symlink is refused"
        let nested = Path.Combine(directory, "nested")
        Directory.CreateDirectory(nested, privateDirectoryMode) |> ignore
        makeFile nested "source.json" (Encoding.UTF8.GetBytes("synthetic")) |> ignore
        let ancestor = Path.Combine(directory, "ancestor-link")
        Directory.CreateSymbolicLink(ancestor, nested) |> ignore
        let viaAncestor = Path.Combine(ancestor, "source.json")

        PrivateFiles.readSource 64 viaAncestor
        |> expectRefused "Symlink ancestor is refused"

        PrivateFiles.readSource 64 nested |> expectRefused "Directory source is refused"

        PrivateFiles.readSource 64 "relative.json"
        |> expectRefused "Relative source is refused")

let private exportIdentityAndCleanupPosix () =
    withSandbox (fun directory ->
        let bytes = Encoding.UTF8.GetBytes("synthetic")
        let destination = Path.Combine(directory, "export.json")
        Expect.equal (PrivateFiles.writeNew destination bytes) (Ok()) "Private export succeeds"
        Expect.equal (File.ReadAllBytes(destination)) bytes "Export preserves exact bytes"
        Expect.equal (File.GetUnixFileMode(destination)) privateFileMode "Export is owner-private"

        PrivateFiles.writeNew destination bytes
        |> expectRefused "Existing export is refused"

        Expect.equal (File.ReadAllBytes(destination)) bytes "Existing export is untouched"
        let failed = Path.Combine(directory, "failed.json")

        PrivateFiles.writeNew failed (Unchecked.defaultof<byte array>)
        |> expectRefused "Write failure is safe"

        Expect.isFalse (File.Exists(failed)) "Only the failed call's partial export is removed"
        let oversized = Path.Combine(directory, "oversized.json")

        PrivateFiles.writeNew oversized (Array.zeroCreate<byte> 131073)
        |> expectRefused "Oversized export is refused"

        Expect.isFalse (File.Exists(oversized)) "Oversized export creates no file")

let private exportSymlinkBoundariesPosix () =
    withSandbox (fun directory ->
        let bytes = Encoding.UTF8.GetBytes("synthetic")
        let target = makeFile directory "target.json" bytes
        let leaf = Path.Combine(directory, "export-link.json")
        File.CreateSymbolicLink(leaf, target) |> ignore
        PrivateFiles.writeNew leaf bytes |> expectRefused "Export symlink is refused"
        Expect.equal (File.ReadAllBytes(target)) bytes "Symlink target is untouched"
        let ancestor = Path.Combine(directory, "ancestor-link")
        Directory.CreateSymbolicLink(ancestor, directory) |> ignore
        let destination = Path.Combine(ancestor, "new.json")

        PrivateFiles.writeNew destination bytes
        |> expectRefused "Export symlink ancestor is refused"

        Expect.isFalse (File.Exists(Path.Combine(directory, "new.json"))) "No file was created")

let private replacementRacePosix () =
    withSandbox (fun directory ->
        let first = Array.create 65536 0x41uy
        let second = Array.create 65536 0x42uy
        let target = makeFile directory "raced.json" first
        use start = new ManualResetEventSlim(false)
        use writerReady = new ManualResetEventSlim(false)

        let writer =
            Task.Run(fun () ->
                start.Wait()

                for index in 1..64 do
                    let bytes = if index % 2 = 0 then first else second
                    let stage = makeFile directory $"stage-{index}.json" bytes
                    File.Move(stage, target, true)
                    writerReady.Set()
                    Thread.Sleep(1))

        start.Set()

        try
            Expect.isTrue (writerReady.Wait(5000)) "Replacement writer becomes active"

            for _ in 1..64 do
                match PrivateFiles.readSource 65536 target with
                | Ok bytes ->
                    Expect.isTrue (bytes = first || bytes = second) "Read pins one complete inode"
                | Error message ->
                    Expect.isFalse
                        (message.Contains(directory, StringComparison.Ordinal))
                        "Race refusal is path-safe"
        finally
            writer.GetAwaiter().GetResult())

let private onSupportedPlatform windows posix =
    if OperatingSystem.IsWindows() then windows () else posix ()

let private privateReadBoundaries () =
    onSupportedPlatform windowsReadRefusal privateReadBoundariesPosix

let private linksAndNonFiles () =
    onSupportedPlatform windowsReadRefusal linksAndNonFilesPosix

let private exportIdentityAndCleanup () =
    onSupportedPlatform windowsWriteRefusal exportIdentityAndCleanupPosix

let private exportSymlinkBoundaries () =
    onSupportedPlatform windowsWriteRefusal exportSymlinkBoundariesPosix

let private replacementRace () =
    onSupportedPlatform windowsReadRefusal replacementRacePosix

let tests =
    testList
        "CLI private-file security"
        [
            testCase
                "[CC-CLI-002] private source enforces exact size, mode, ACL, and strict UTF-8"
                privateReadBoundaries
            testCase
                "[CC-CLI-002] private source rejects symlinks, ancestor links, directories, and relative paths"
                linksAndNonFiles
            testCase
                "[CC-CLI-002] private export is exclusive, owner-only, and removes its failed partial file"
                exportIdentityAndCleanup
            testCase
                "[CC-CLI-002] private export rejects leaf and ancestor links without changing targets"
                exportSymlinkBoundaries
            testCase
                "[CC-CLI-002] concurrent source replacement yields one complete inode or a safe refusal"
                replacementRace
        ]
