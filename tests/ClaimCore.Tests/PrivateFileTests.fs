module ClaimCore.Tests.PrivateFileTests

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open System.Runtime.Loader
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.HostSecurity
open Microsoft.Win32.SafeHandles

let private file mode (content: byte array) action =
    let directory = Directory.CreateTempSubdirectory("claimcore-private-file-")
    let path = Path.Combine(directory.FullName, "credential")

    try
        File.WriteAllBytes(path, content)
        File.SetUnixFileMode(path, mode)
        action path
    finally
        Directory.Delete(directory.FullName, true)

let private setPosixAcl path permissions =
    let start = ProcessStartInfo("setfacl")

    for argument in [ "-m"; permissions; path ] do
        start.ArgumentList.Add argument

    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    use child = Process.Start start |> nonNull

    if not (child.WaitForExit 3000) then
        child.Kill(true)
        child.WaitForExit()
        failtest "Owned POSIX ACL fixture setup exceeded its bound"

    Expect.equal child.ExitCode 0 "The acl test prerequisite applied the owned fixture"

[<Tests>]
let tests =
    testList
        "owner-private files"
        [
            testCase
                "native scalar statuses retain typed refusals and unknown statuses refuse"
                (fun () ->
                    let failures =
                        [
                            PrivateFileFailure.Missing
                            PrivateFileFailure.IsLink
                            PrivateFileFailure.NotRegular
                            PrivateFileFailure.WrongOwner
                            PrivateFileFailure.TooPermissive
                            PrivateFileFailure.Unreadable
                            PrivateFileFailure.AclRefused
                        ]

                    Expect.equal
                        (PrivateFileNative.status 0)
                        (Ok())
                        "Only admitted status succeeds"

                    for index, failure in List.indexed failures do
                        Expect.equal
                            (PrivateFileNative.status (index + 1))
                            (Error failure)
                            "Typed refusal"

                    for unknown in [ -1; 8; Int32.MaxValue ] do
                        Expect.equal
                            (PrivateFileNative.status unknown)
                            (Error PrivateFileFailure.Unreadable)
                            "Unknown native status cannot admit")
            testCase
                "missing and incompatible native loaders refuse with fixed safe guidance"
                (fun () ->
                    for failure in
                        [
                            DllNotFoundException("private loader detail") :> exn
                            EntryPointNotFoundException("private symbol detail")
                            BadImageFormatException("private binary detail")
                        ] do
                        Expect.equal
                            (PrivateFileNative.invoke (fun () -> raise failure))
                            (Error PrivateFileFailure.NativeAssetUnavailable)
                            "Loader failure is typed without leaking its exception"

                        Expect.isFalse
                            (PrivateFile.nativeAssetGuidance.Contains failure.Message)
                            "Guidance omits loader exception detail"

                    Expect.stringContains
                        PrivateFile.nativeAssetGuidance
                        "Rebuild or reinstall"
                        "Actionable")
            testCase "actual missing and wrong native assets retain safe typed failure" (fun () ->
                file UnixFileMode.UserRead [| 42uy |] (fun wrongAsset ->
                    for asset in [ wrongAsset + "-absent"; wrongAsset ] do
                        let context = AssemblyLoadContext("private-file-loader-control", true)

                        try
                            let assembly =
                                context.LoadFromAssemblyPath(
                                    typeof<PrivateFileFailure>.Assembly.Location
                                )

                            NativeLibrary.SetDllImportResolver(
                                assembly,
                                DllImportResolver(fun _ _ _ -> NativeLibrary.Load asset)
                            )

                            let owner =
                                assembly.GetType("ClaimCore.HostSecurity.PrivateFile") |> nonNull

                            let read = owner.GetMethod("read") |> nonNull
                            let result = read.Invoke(null, [| box 1; box wrongAsset |]) |> nonNull

                            let case, fields =
                                FSharpValue.GetUnionFields(result, result.GetType())

                            Expect.equal case.Name "Error" "Actual P/Invoke loader refuses"

                            Expect.equal
                                (fields |> Array.map string)
                                [| "NativeAssetUnavailable" |]
                                "Exact safe failure without native exception detail"
                        finally
                            context.Unload()))
            testCase
                "negative bounds and malformed paths refuse without rewriting valid Unicode"
                (fun () ->
                    Expect.equal
                        (PrivateFile.read -1 "/unopened")
                        (Error PrivateFileFailure.InvalidLimit)
                        "Negative bound refused"

                    file UnixFileMode.UserRead [| 42uy |] (fun path ->
                        Expect.equal
                            (PrivateFile.read 1 (path + "\000-trailing"))
                            (Error PrivateFileFailure.InvalidPath)
                            "Native C string must not shorten a path"

                        let replacement = path + "\uFFFD"
                        File.WriteAllBytes(replacement, [| 99uy |])
                        File.SetUnixFileMode(replacement, UnixFileMode.UserRead)

                        for scalar in [ 0xD800; 0xDC00 ] do
                            Expect.equal
                                (PrivateFile.read 1 (path + String([| char scalar |])))
                                (Error PrivateFileFailure.InvalidPath)
                                "Malformed UTF16 cannot select the replacement-character file"

                        Expect.equal
                            (PrivateFile.read 1 replacement)
                            (Ok [| 99uy |])
                            "A genuine replacement-character path is valid"

                        let valid = path + "é-索赔-💼"
                        File.WriteAllBytes(valid, [| 99uy |])
                        File.SetUnixFileMode(valid, UnixFileMode.UserRead)

                        Expect.equal
                            (PrivateFile.read 1 valid)
                            (Ok [| 99uy |])
                            "Exact Unicode path"))
            testCase "native descriptor and platform ACL controls pass" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-private-controls-")

                try
                    let start =
                        ProcessStartInfo(
                            Path.Combine(
                                AppContext.BaseDirectory,
                                "claimcore-private-file-controls"
                            )
                        )

                    start.ArgumentList.Add(Path.Combine(directory.FullName, "fixture"))
                    start.RedirectStandardOutput <- true
                    start.RedirectStandardError <- true
                    use child = Process.Start start |> nonNull

                    if not (child.WaitForExit 5000) then
                        child.Kill(true)
                        child.WaitForExit()
                        failtest "Native private-file controls exceeded their bound"

                    Expect.equal child.ExitCode 0 "Actual platform controls"

                    Expect.equal
                        (child.StandardOutput.ReadToEnd())
                        "PRIVATE_FILE_CONTROLS_OK\n"
                        "Control marker"

                    Expect.equal (child.StandardError.ReadToEnd()) "" "No diagnostic output"
                finally
                    directory.Delete(true))
            testCase
                "managed ownership closes handles after success, refusal and stream construction failure"
                (fun () ->
                    file UnixFileMode.UserRead [| 42uy |] (fun path ->
                        for limit, expected in
                            [ 1, Ok [| 42uy |]; 0, Error PrivateFileFailure.TooLarge ] do
                            let descriptor =
                                PrivateFileNative.openRead path
                                |> Result.defaultWith (fun _ ->
                                    failtest "Synthetic descriptor not admitted")

                            let owned = new SafeFileHandle(nativeint descriptor, true)

                            try
                                Expect.equal
                                    (RandomAccess.GetLength owned)
                                    1L
                                    "Handle starts open"

                                Expect.equal
                                    (PrivateFile.readHandle limit owned)
                                    expected
                                    "Read result"

                                Expect.isTrue
                                    owned.IsClosed
                                    "Stream disposed the sole owning handle"
                            finally
                                if not owned.IsClosed then
                                    owned.Dispose())

                    let invalid = new SafeFileHandle(nativeint -1, true)

                    Expect.throwsT<ArgumentException>
                        (fun () -> PrivateFile.readHandle 1 invalid |> ignore)
                        "Invalid handle refuses stream construction"

                    Expect.isTrue
                        invalid.IsClosed
                        "Construction failure disposed its owning handle")
            testCase "Linux POSIX ACL mask is reflected in the admitted descriptor mode" (fun () ->
                if OperatingSystem.IsLinux() then
                    file (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) [| 42uy |] (fun path ->
                        setPosixAcl path "u:65534:r--,m::---"

                        Expect.equal
                            (File.GetUnixFileMode path &&& UnixFileMode.GroupRead)
                            UnixFileMode.None
                            "Zero ACL mask has no group-class read bit"

                        Expect.equal (PrivateFile.read 1 path) (Ok [| 42uy |]) "Masked ACL admits"
                        setPosixAcl path "m::r--"

                        Expect.equal
                            (File.GetUnixFileMode path &&& UnixFileMode.GroupRead)
                            UnixFileMode.GroupRead
                            "Effective ACL mask exposes group-class read"

                        Expect.equal
                            (PrivateFile.read 1 path)
                            (Error PrivateFileFailure.TooPermissive)
                            "Nonzero named-user access refuses"))
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
