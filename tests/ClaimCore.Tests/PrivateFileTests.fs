module ClaimCore.Tests.PrivateFileTests

open System
open System.IO
open System.Diagnostics
open System.Net.Sockets
open System.Security.Cryptography
open Expecto
open ClaimCore.HostSecurity
open ClaimCore.TestSupport

let private owner = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private file mode (content: byte array) action =
    let directory = Directory.CreateTempSubdirectory("claimcore-private-file-")
    let path = Path.Combine(directory.FullName, "credential")

    try
        File.WriteAllBytes(path, content)
        File.SetUnixFileMode(path, mode)
        action path
    finally
        directory.Delete(true)

let private probe mode path =
    let start =
        ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true)

    for argument in
        [
            "fsi"
            "--reference:" + typeof<PrivateFileFailure>.Assembly.Location
            "--exec"
            Path.Combine(RepositoryRoot.find (), "tests/ClaimCore.Tests/PrivateFileProbe.fsx")
            mode
            path
        ] do
        start.ArgumentList.Add argument

    use child = Process.Start start |> nonNull

    if not (child.WaitForExit 30000) then
        child.Kill(true)
        failtest "Private-file subprocess exceeded its watchdog"

    Expect.equal child.ExitCode 0 "Isolated ownership and bounded nonregular-file refusal"

let private pathControls () =
    for limit in [ -1; 0 ] do
        Expect.equal
            (PrivateFile.read limit "/unopened")
            (Error PrivateFileFailure.InvalidLimit)
            "Positive bound required"

    Expect.equal
        (PrivateFile.read 1 "relative")
        (Error PrivateFileFailure.NotAbsolute)
        "Absolute path"

    file owner [| 42uy |] (fun path ->
        for invalid in
            [
                path + "\000"
                path + String([| char 0xD800 |])
                path + String([| char 0xDC00 |])
            ] do
            Expect.equal
                (PrivateFile.read 1 invalid)
                (Error PrivateFileFailure.InvalidPath)
                "Exact Unicode path admission"

        Expect.equal
            (PrivateFile.read 1 (path + "-absent"))
            (Error PrivateFileFailure.Missing)
            "Missing file"

        let unicode = path + "é-索赔-💼"
        File.Move(path, unicode)
        Expect.equal (PrivateFile.read 1 unicode) (Ok [| 42uy |]) "Unicode spelling retained")

let private handles () =
    let opened path =
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

    file owner [| 42uy |] (fun path ->
        for maximum, expected in [ 1, Ok [| 42uy |]; 0, Error PrivateFileFailure.TooLarge ] do
            let handle = opened path

            Expect.equal
                (PrivateFile.readHandle maximum 1L handle)
                expected
                "One handle supplies the bounded bytes"

            Expect.isTrue handle.IsClosed "Success and refusal close the handle"

        let changed = opened path

        Expect.equal
            (PrivateFile.readHandle 1 2L changed)
            (Error PrivateFileFailure.Unreadable)
            "Length changes refuse"

        Expect.isTrue changed.IsClosed "Length refusal closes the handle"
        let handle = opened path
        File.SetUnixFileMode(path, owner ||| UnixFileMode.OtherRead)

        Expect.equal
            (PrivateFile.readHandle 1 1L handle)
            (Error PrivateFileFailure.TooPermissive)
            "Mode checked on the handle"

        Expect.isTrue handle.IsClosed "Permission refusal closes the handle")

    file owner [| 42uy |] (fun path ->
        let handle = opened path
        File.Move(path, path + "-original")
        File.WriteAllBytes(path, [| 43uy |])
        File.SetUnixFileMode(path, owner ||| UnixFileMode.OtherRead)

        Expect.equal
            (PrivateFile.readHandle 1 1L handle)
            (Ok [| 42uy |])
            "A replacement path cannot substitute handle bytes")

let private failedRead () =
    use truncated = new MemoryStream([| 42uy |])
    let destination = Array.create 2 99uy

    Expect.throwsT<EndOfStreamException>
        (fun () ->
            PrivateFile.readBytes
                (fun buffer -> truncated.ReadExactly(buffer, 0, buffer.Length))
                destination
            |> ignore)
        "Partial reads fail"

    Expect.equal destination [| 0uy; 0uy |] "Partial content and unused bytes are cleared"

let private fifo () =
    let directory = Directory.CreateTempSubdirectory("claimcore-fifo-")
    let path = Path.Combine(directory.FullName, "fifo")

    try
        let start = ProcessStartInfo("mkfifo")
        start.ArgumentList.Add path
        use child = Process.Start start |> nonNull
        child.WaitForExit()
        Expect.equal child.ExitCode 0 "Owned FIFO created"
        File.SetUnixFileMode(path, owner)
        probe "refusals" path
    finally
        directory.Delete(true)

let private socket () =
    let directory = Directory.CreateTempSubdirectory("claimcore-socket-")

    try
        let path = Path.Combine(directory.FullName, "socket")

        use listener =
            new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

        listener.Bind(UnixDomainSocketEndPoint(path))
        File.SetUnixFileMode(path, owner)

        Expect.equal
            (PrivateFile.read 1 path)
            (Error PrivateFileFailure.NotRegular)
            "Zero-length socket refuses before open"
    finally
        directory.Delete(true)

[<Tests>]
let tests =
    testList
        "managed private files"
        [
            testCase "private regular files and group-read files admit" (fun () ->
                for mode in [ owner; owner ||| UnixFileMode.GroupRead ] do
                    file mode [| 42uy |] (fun path ->
                        Expect.equal (PrivateFile.read 1 path) (Ok [| 42uy |]) "Admitted mask"))
            testCase "every forbidden permission bit refuses" (fun () ->
                for bit in
                    [
                        UnixFileMode.OtherRead
                        UnixFileMode.OtherWrite
                        UnixFileMode.OtherExecute
                        UnixFileMode.GroupWrite
                        UnixFileMode.GroupExecute
                    ] do
                    file (owner ||| bit) [| 42uy |] (fun path ->
                        Expect.equal
                            (PrivateFile.read 1 path)
                            (Error PrivateFileFailure.TooPermissive)
                            "Forbidden mask"))
            testCase "symlinks follow final bytes and permissions" (fun () ->
                file owner [| 42uy |] (fun path ->
                    let link = path + "-link"
                    File.CreateSymbolicLink(link, path) |> ignore

                    Expect.equal
                        (PrivateFile.read 1 link)
                        (Ok [| 42uy |])
                        "Final target length, not link spelling length"

                    File.SetUnixFileMode(path, owner ||| UnixFileMode.OtherRead)

                    Expect.equal
                        (PrivateFile.read 1 link)
                        (Error PrivateFileFailure.TooPermissive)
                        "Final target permissions"))
            testCase "empty and oversized regular inputs refuse" (fun () ->
                file owner [||] (fun path ->
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.NotRegular)
                        "Empty input")

                file owner [| 1uy; 2uy |] (fun path ->
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.TooLarge)
                        "Bounded input"))
            testCase "directories refuse" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-private-directory-")

                try
                    Expect.equal
                        (PrivateFile.read 1 directory.FullName)
                        (Error PrivateFileFailure.NotRegular)
                        "Directory"
                finally
                    directory.Delete())
            testCase "devices refuse before opening" (fun () ->
                for path in [ "/dev/null"; "/dev/zero" ] do
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.NotRegular)
                        "Zero-length device")
            testCase
                "FIFO refusals finish within three seconds without handle or worker growth"
                fifo
            testCase "sockets refuse before opening" socket
            testCase "path and size admission" pathControls
            testCase "handle disposal mode length and substitution" handles
            testCase "partial reads zero their complete buffer" failedRead
            testCase "descriptor zero remains an owned valid file handle" (fun () ->
                file owner [| 42uy |] (probe "zero"))
            testCase "Windows refusal precedes IO" (fun () ->
                Expect.equal
                    (PrivateFile.readOnPlatform false 1 "/unopened")
                    (Error PrivateFileFailure.UnsupportedPlatform)
                    "Platform boundary")
        ]
