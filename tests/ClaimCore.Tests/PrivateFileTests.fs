module ClaimCore.Tests.PrivateFileTests

open System
open System.IO
open System.Diagnostics
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
        Directory.Delete(directory.FullName, true)

[<Tests>]
let tests =
    testList
        "owner-private files"
        [
            testCase "0600 and 0400 bounded regular files are admitted" (fun () ->
                for mode in
                    [ UnixFileMode.UserRead; UnixFileMode.UserRead ||| UnixFileMode.UserWrite ] do
                    file mode [| 42uy |] (fun path ->
                        Expect.equal (PrivateFile.read 1 path) (Ok [| 42uy |]) "Exact bytes"))
            for bit in
                [
                    UnixFileMode.GroupRead
                    UnixFileMode.GroupWrite
                    UnixFileMode.GroupExecute
                    UnixFileMode.OtherRead
                    UnixFileMode.OtherWrite
                    UnixFileMode.OtherExecute
                ] do
                testCase ($"group/other mode {bit} is refused") (fun () ->
                    file (UnixFileMode.UserRead ||| bit) [| 42uy |] (fun path ->
                        Expect.equal
                            (PrivateFile.read 1 path)
                            (Error PrivateFileFailure.TooPermissive)
                            "Mode belongs to open handle"))
            testCase "oversized files are refused and empty bounded files are allowed" (fun () ->
                file UnixFileMode.UserRead [| 1uy; 2uy |] (fun path ->
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.TooLarge)
                        "Bound enforced")

                file UnixFileMode.UserRead [||] (fun path ->
                    Expect.equal (PrivateFile.read 0 path) (Ok [||]) "Empty regular file"))
            testCase "relative and missing paths are refused" (fun () ->
                Expect.equal
                    (PrivateFile.read 1 "relative")
                    (Error PrivateFileFailure.NotAbsolute)
                    "Absolute path required"

                let directory = Directory.CreateTempSubdirectory("claimcore-missing-file-")

                try
                    Expect.equal
                        (PrivateFile.read 1 (Path.Combine(directory.FullName, "missing")))
                        (Error PrivateFileFailure.Missing)
                        "Missing file"
                finally
                    directory.Delete())
            testCase "symbolic links including dangling links are refused" (fun () ->
                file UnixFileMode.UserRead [| 1uy |] (fun path ->
                    for target in [ path; path + "-absent" ] do
                        let link = path + "-link"
                        File.CreateSymbolicLink(link, target) |> ignore

                        try
                            Expect.equal
                                (PrivateFile.read 1 link)
                                (Error PrivateFileFailure.IsLink)
                                "Link is never admitted"
                        finally
                            File.Delete link))
            testCase "unreadable private files are refused" (fun () ->
                file UnixFileMode.None [| 1uy |] (fun path ->
                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.Unreadable)
                        "Unreadable file"))
            testCase "FIFO refusal does not wait for a writer" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-fifo-")

                try
                    let path = Path.Combine(directory.FullName, "fifo")
                    let start = ProcessStartInfo("mkfifo")
                    start.ArgumentList.Add path
                    use child = Process.Start start |> nonNull
                    child.WaitForExit()
                    Expect.equal child.ExitCode 0 "Fixture FIFO created"
                    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    let watch = Stopwatch.StartNew()

                    Expect.equal
                        (PrivateFile.read 1 path)
                        (Error PrivateFileFailure.NotRegular)
                        "FIFO is not a credential"

                    Expect.isLessThan watch.Elapsed (TimeSpan.FromSeconds 3.) "Open must not block"
                finally
                    directory.Delete(true))
            testCase "directories are not regular files" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-directory-")

                try
                    Expect.equal
                        (PrivateFile.read 1 directory.FullName)
                        (Error PrivateFileFailure.NotRegular)
                        "Directory is not a file"
                finally
                    directory.Delete())
            testCase "unsupported platforms fail before file IO" (fun () ->
                Expect.equal
                    (PrivateFile.readOnPlatform false 1 "/unopened")
                    (Error PrivateFileFailure.UnsupportedPlatform)
                    "Platform refusal")
        ]
