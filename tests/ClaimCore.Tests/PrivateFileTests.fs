module ClaimCore.Tests.PrivateFileTests

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Security.Cryptography
open Expecto
open ClaimCore.HostSecurity
open ClaimCore.TestSupport

let private file mode (content: byte array) action =
    let directory = Directory.CreateTempSubdirectory("claimcore-private-file-")
    let path = Path.Combine(directory.FullName, "credential")

    try
        File.WriteAllBytes(path, content)
        File.SetUnixFileMode(path, mode)
        action path
    finally
        directory.Delete(true)

let private fifo mode =
    let directory = Directory.CreateTempSubdirectory("claimcore-fifo-")
    let path = Path.Combine(directory.FullName, "fifo")
    let start = ProcessStartInfo("mkfifo")
    start.ArgumentList.Add path
    use child = Process.Start start |> nonNull
    child.WaitForExit()
    Expect.equal child.ExitCode 0 "Owned FIFO created"
    File.SetUnixFileMode(path, mode)

    try
        for _ in 1..100 do
            Expect.equal
                (PrivateFile.read 1 path)
                (Error PrivateFileFailure.NotRegular)
                "FIFO refuses without a writer or abandoned worker"
    finally
        directory.Delete(true)

let private pathControls () =
    Expect.equal (PrivateFile.read -1 "/unopened") (Error PrivateFileFailure.InvalidLimit) "Bound"
    Expect.equal (PrivateFile.read 1 "relative") (Error PrivateFileFailure.NotAbsolute) "Absolute"

    file UnixFileMode.UserRead [| 42uy |] (fun path ->
        Expect.equal
            (PrivateFile.read 1 (path + "\000"))
            (Error PrivateFileFailure.InvalidPath)
            "NUL"

        for scalar in [ 0xD800; 0xDC00 ] do
            Expect.equal
                (PrivateFile.read 1 (path + String([| char scalar |])))
                (Error PrivateFileFailure.InvalidPath)
                "Malformed UTF16"

        Expect.equal
            (PrivateFile.read 1 (path + "-absent"))
            (Error PrivateFileFailure.Missing)
            "Missing"

        let unicode = path + "é-索赔-💼"
        File.Move(path, unicode)
        Expect.equal (PrivateFile.read 1 unicode) (Ok [| 42uy |]) "Exact Unicode path")

let private handles () =
    let abi = UnixPrivateFile.platform () |> Option.get

    let openHandle path =
        UnixPrivateFile.openRead abi (Encoding.UTF8.GetBytes(path + "\000"))

    file UnixFileMode.UserRead [| 42uy |] (fun path ->
        for maximum, expected in [ 1, Ok [| 42uy |]; 0, Error PrivateFileFailure.TooLarge ] do
            let handle = openHandle path
            Expect.equal (PrivateFile.readHandle maximum abi handle) expected "Same handle read"
            Expect.isTrue handle.IsClosed "Owning handle closed on success and refusal"

        let handle = openHandle path
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.OtherRead)

        Expect.equal
            (PrivateFile.readHandle 1 abi handle)
            (Error PrivateFileFailure.TooPermissive)
            "Handle mode is checked independently of path admission"

        Expect.isTrue handle.IsClosed "Mode refusal disposes the handle")

    file UnixFileMode.UserRead [| 42uy |] (fun path ->
        let handle = openHandle path
        File.Move(path, path + "-original")
        File.WriteAllBytes(path, [| 43uy |])
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.OtherRead)

        Expect.equal
            (PrivateFile.readHandle 1 abi handle)
            (Ok [| 42uy |])
            "Replacement path never substitutes the admitted descriptor")

let private failedRead () =
    use truncated = new MemoryStream([| 42uy |])
    let destination = Array.create 2 99uy

    Expect.throwsT<EndOfStreamException>
        (fun () ->
            PrivateFile.readBytes
                (fun buffer -> truncated.ReadExactly(buffer, 0, buffer.Length))
                destination
            |> ignore)
        "A partial read fails rather than returning a shortened input"

    Expect.equal destination [| 0uy; 0uy |] "A failed read clears partial content and unused bytes"

let private probe mode path =
    let start =
        ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true)

    let script =
        Path.Combine(RepositoryRoot.find (), "tests/ClaimCore.Tests/PrivateFileProbe.fsx")

    [
        "fsi"
        "--reference:" + typeof<PrivateFileFailure>.Assembly.Location
        "--exec"
        script
        mode
        path
    ]
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start |> nonNull

    if not (child.WaitForExit 30000) then
        child.Kill(true)
        failtest "Private-file subprocess exceeded its watchdog"

    Expect.equal child.ExitCode 0 "Isolated native ownership and resource controls"

let private descriptorZero () =
    file UnixFileMode.UserRead [| 42uy |] (probe "zero")

[<DllImport("libc", EntryPoint = "fcntl")>]
extern int private descriptorFlags(SafeHandle handle, int command)

[<DllImport("libc", EntryPoint = "lseek", SetLastError = true)>]
extern int64 private seekDescriptor(SafeHandle handle, int64 offset, int origin)

let private openFlags () =
    file UnixFileMode.UserRead [| 42uy |] (fun path ->
        let abi = UnixPrivateFile.platform () |> Option.get
        use handle = UnixPrivateFile.openRead abi (Encoding.UTF8.GetBytes(path + "\000"))
        let flags = descriptorFlags (handle, 3)
        let nonblocking = if OperatingSystem.IsMacOS() then 4 else 0x800
        Expect.equal (flags &&& nonblocking) nonblocking "Open does not wait for a FIFO peer"
        Expect.equal (flags &&& 3) 0 "Descriptor is read-only"
        Expect.equal (descriptorFlags (handle, 1) &&& 1) 1 "Descriptor closes across exec")

let private interruptedCalls () =
    let mutable calls = 0

    let result =
        UnixPrivateFile.interrupted (fun () ->
            calls <- calls + 1
            Marshal.SetLastPInvokeError(if calls < 3 then 4 else 0)
            if calls < 3 then -1 else 42)

    Expect.equal (result, calls) (42, 3) "EINTR retries before success"
    Marshal.SetLastPInvokeError 5
    Expect.equal (UnixPrivateFile.interrupted (fun () -> -1)) -1 "Other errors are preserved"

let private socket () =
    let directory = Directory.CreateTempSubdirectory("claimcore-private-socket-")

    try
        let path = Path.Combine(directory.FullName, "socket")

        use listener =
            new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

        listener.Bind(UnixDomainSocketEndPoint(path))
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        Expect.equal
            (PrivateFile.read 1 path)
            (Error PrivateFileFailure.Unreadable)
            "Socket cannot open as a file"
    finally
        directory.Delete(true)

let private device () =
    let abi = UnixPrivateFile.platform () |> Option.get
    use handle = UnixPrivateFile.openRead abi (Encoding.UTF8.GetBytes("/dev/null\000"))
    Expect.equal (seekDescriptor (handle, 0L, 0)) 0L "Read-only seekable system device control"
    let mode, _ = UnixPrivateFile.metadata abi handle

    Expect.equal
        (mode &&& 0xF000)
        0x2000
        "Existing device classified read-only without changing permissions"

    Expect.equal
        (PrivateFile.read 0 "/dev/null")
        (Error PrivateFileFailure.NotRegular)
        "Character device refuses"

let private platformIdentity () =
    let actual =
        match UnixPrivateFile.platform () with
        | Some UnixPrivateFile.GlibcIntel -> "linux-x64"
        | Some UnixPrivateFile.GlibcArm -> "linux-arm64"
        | Some UnixPrivateFile.DarwinIntel -> "osx-x64"
        | Some UnixPrivateFile.DarwinArm -> "osx-arm64"
        | None -> failtest "Private-file test platform has no admitted ABI"

    match Environment.GetEnvironmentVariable "CLAIMCORE_PRIVATE_FILE_RID" with
    | null -> ()
    | expected -> Expect.equal actual expected "Qualification uses the scheduled native ABI"

let private concurrentGrowth () =
    file (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) [| 42uy |] (fun path ->
        let writer =
            System.Threading.Tasks.Task.Run(fun () ->
                for length in 0..200 do
                    File.WriteAllBytes(path, Array.create length 42uy))

        for _ in 1..200 do
            match PrivateFile.read 1 path with
            | Ok bytes ->
                Expect.isLessThanOrEqual bytes.Length 1 "Concurrent growth cannot exceed the cap"
            | Error PrivateFileFailure.TooLarge
            | Error PrivateFileFailure.Unreadable -> ()
            | Error _ -> failtest "Owned regular file unexpectedly refused its type or mode"

        writer.GetAwaiter().GetResult())

[<Tests>]
let tests =
    testList
        "Unix private files"
        [
            testCase "scheduled platform ABI is exercised" platformIdentity
            testCase "partial read failure clears the complete buffer" failedRead
            testCase "path admission" pathControls
            testCase "concurrent file growth stays bounded" concurrentGrowth
            testCase "descriptor zero is owned and closed" descriptorZero
            testCase "native descriptor flags" openFlags
            testCase "interrupted syscalls retry" interruptedCalls
            testCase "repeated refusals retain no descriptors or workers" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-private-refusals-")

                try
                    probe "refusals" directory.FullName
                finally
                    directory.Delete())
            testCase "Unix sockets refuse" socket
            testCase "existing device classification is read-only" device
            testCase "same-handle admission and disposal" handles
            for mode in
                [
                    UnixFileMode.UserRead
                    UnixFileMode.UserRead ||| UnixFileMode.UserExecute
                    UnixFileMode.UserRead ||| UnixFileMode.SetUser
                    UnixFileMode.UserRead ||| UnixFileMode.SetGroup
                    UnixFileMode.UserRead ||| UnixFileMode.StickyBit
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                    UnixFileMode.UserRead ||| UnixFileMode.GroupRead
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
                ] do
                testCase ($"mode {mode} admits") (fun () ->
                    file mode [| 42uy |] (fun path ->
                        let bytes =
                            PrivateFile.read 1 path
                            |> Result.defaultWith (fun _ -> failtest "Refused")

                        Expect.equal bytes [| 42uy |] "Exact bytes"
                        CryptographicOperations.ZeroMemory(Span<byte> bytes)
                        Expect.equal bytes [| 0uy |] "Caller clears its buffer"))
            for bit in
                [
                    UnixFileMode.GroupWrite
                    UnixFileMode.GroupExecute
                    UnixFileMode.OtherRead
                    UnixFileMode.OtherWrite
                    UnixFileMode.OtherExecute
                ] do
                testCase ($"mode bit {bit} refuses") (fun () ->
                    let mode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| bit

                    let mode =
                        if bit = UnixFileMode.GroupWrite || bit = UnixFileMode.OtherRead then
                            mode ||| UnixFileMode.GroupRead
                        else
                            mode

                    file mode [| 42uy |] (fun path ->
                        Expect.equal
                            (PrivateFile.read 1 path)
                            (Error PrivateFileFailure.TooPermissive)
                            "No other access or group writes/execute"))
            testCase "size bounds include empty files" (fun () ->
                file UnixFileMode.UserRead [| 1uy; 2uy |] (fun path ->
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.TooLarge)
                        "Maximum")

                file UnixFileMode.UserRead [||] (fun path ->
                    Expect.equal (PrivateFile.read 0 path) (Ok [||]) "Empty"))
            testCase "symlinks follow the target's admission" (fun () ->
                file UnixFileMode.UserRead [| 42uy |] (fun path ->
                    let link = path + "-link"
                    File.CreateSymbolicLink(link, path) |> ignore

                    Expect.equal
                        (PrivateFile.read 1 link)
                        (Ok [| 42uy |])
                        "Secret-volume links admit"

                    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.OtherRead)

                    Expect.equal
                        (PrivateFile.read 1 link)
                        (Error PrivateFileFailure.TooPermissive)
                        "Target mode"))
            testCase "directories refuse" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-private-directory-")

                try
                    File.SetUnixFileMode(
                        directory.FullName,
                        UnixFileMode.UserRead ||| UnixFileMode.UserExecute
                    )

                    Expect.equal
                        (PrivateFile.read 1 directory.FullName)
                        (Error PrivateFileFailure.NotRegular)
                        "Directory"
                finally
                    directory.Delete())
            testCase "private FIFO refuses before a writer opens" (fun () ->
                fifo (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
            testCase "public FIFO refuses as nonregular" (fun () ->
                fifo (
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.GroupRead
                    ||| UnixFileMode.OtherRead
                ))
            testCase "unsupported platforms refuse before IO" (fun () ->
                Expect.equal
                    (PrivateFile.readOnPlatform false 1 "/unopened")
                    (Error PrivateFileFailure.UnsupportedPlatform)
                    "Unsupported platform")
        ]
