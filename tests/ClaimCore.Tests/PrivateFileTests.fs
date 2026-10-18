module ClaimCore.Tests.PrivateFileTests

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Security.Cryptography
open Expecto
open ClaimCore.HostSecurity

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
        let watch = Stopwatch.StartNew()

        let expected =
            if mode.HasFlag UnixFileMode.OtherRead then
                PrivateFileFailure.TooPermissive
            else
                PrivateFileFailure.NotRegular

        Expect.equal (PrivateFile.read 1 path) (Error expected) "FIFO refuses"
        Expect.isLessThan watch.Elapsed (TimeSpan.FromSeconds 3.) "Bounded open"
    finally
        // Complete the timed-out owned open so its continuation can dispose the late handle.
        if not (mode.HasFlag UnixFileMode.OtherRead) then
            use writer = File.OpenHandle(path, FileMode.Open, FileAccess.Write)
            ()

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
    file UnixFileMode.UserRead [| 42uy |] (fun path ->
        for maximum, expected in [ 1, Ok [| 42uy |]; 0, Error PrivateFileFailure.TooLarge ] do
            let handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read)
            Expect.equal (PrivateFile.readHandle maximum handle) expected "Same handle read"
            Expect.isTrue handle.IsClosed "Owning handle closed on success and refusal"

        let handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read)
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.OtherRead)

        Expect.equal
            (PrivateFile.readHandle 1 handle)
            (Error PrivateFileFailure.TooPermissive)
            "Handle mode is checked independently of path admission"

        Expect.isTrue handle.IsClosed "Mode refusal disposes the handle")

[<Tests>]
let tests =
    testList
        "managed private files"
        [
            testCase "path admission" pathControls
            testCase "same-handle admission and disposal" handles
            for mode in
                [
                    UnixFileMode.UserRead
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                    UnixFileMode.UserRead ||| UnixFileMode.GroupRead
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
                    file (UnixFileMode.UserRead ||| bit) [| 42uy |] (fun path ->
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
            testCase "private FIFO open is bounded" (fun () ->
                fifo (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
            testCase "public FIFO refuses before open" (fun () ->
                fifo (UnixFileMode.UserRead ||| UnixFileMode.OtherRead))
            testCase "unsupported platforms refuse before IO" (fun () ->
                Expect.equal
                    (PrivateFile.readOnPlatform false 1 "/unopened")
                    (Error PrivateFileFailure.UnsupportedPlatform)
                    "Unsupported platform")
        ]
