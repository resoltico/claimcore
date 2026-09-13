module ClaimCore.Tests.NativePrivateFileRaceTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.HostSecurity

let private physicalTemp () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (temporary.StartsWith("/var/", StringComparison.Ordinal)
            || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + temporary
    else
        temporary

let private withSandbox action =
    let root =
        Path.Combine(physicalTemp (), "claimcore-native-test-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(root, mode) |> ignore

    try
        action root mode
    finally
        Directory.Delete(root, true)

let private pinnedAncestorSwap () =
    if OperatingSystem.IsWindows() then
        let destination =
            Path.Combine(Path.GetTempPath(), "claimcore-unsupported-native-test")

        match PrivateFiles.writeNew destination (Encoding.UTF8.GetBytes("synthetic")) with
        | Error _ ->
            Expect.isFalse (File.Exists(destination)) "Windows private creation fails closed"
        | Ok() -> failtest "Windows private creation must not be supported"
    else
        withSandbox (fun root mode ->
            let pinned = Path.Combine(root, "pinned")
            let moved = Path.Combine(root, "moved")
            let redirect = Path.Combine(root, "redirect")
            Directory.CreateDirectory(pinned, mode) |> ignore
            Directory.CreateDirectory(redirect, mode) |> ignore
            let destination = Path.Combine(pinned, "export.json")
            let bytes = Encoding.UTF8.GetBytes("synthetic")

            PosixPrivateFiles.withParent destination (fun parent leaf ->
                Directory.Move(pinned, moved)
                Directory.CreateSymbolicLink(pinned, redirect) |> ignore
                let descriptor = int (parent.DangerousGetHandle())
                use created = PosixPrivateNative.createPrivateHandle descriptor leaf
                use stream = new FileStream(created, FileAccess.Write)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)

                let opened =
                    PosixPrivateNative.privateRegular (stream.SafeFileHandle.DangerousGetHandle())

                Expect.isTrue (opened.Mode &&& 0o077u = 0u) "Created inode is owner-private"

                let locked, wasCreated =
                    PosixPrivateNative.openLockedHandle descriptor "state.lock"

                use _lock = locked
                Expect.isTrue wasCreated "Pinned parent receives the exclusive lock")

            Expect.isTrue
                (File.Exists(Path.Combine(moved, "export.json")))
                "Export stays in pinned parent"

            Expect.isFalse
                (File.Exists(Path.Combine(redirect, "export.json")))
                "Redirect receives no bytes"

            Expect.isTrue
                (File.Exists(Path.Combine(moved, "state.lock")))
                "Lock stays in pinned parent"

            Expect.isFalse
                (File.Exists(Path.Combine(redirect, "state.lock")))
                "Redirect receives no lock"

            match PrivateFiles.writeNew (Path.Combine(pinned, "second.json")) bytes with
            | Error _ ->
                Expect.isFalse
                    (File.Exists(Path.Combine(redirect, "second.json")))
                    "Linked ancestor is rejected"
            | Ok() -> failtest "A swapped ancestor must not be followed")

let tests =
    testList
        "native private-file ancestor race"
        [
            testCase
                "[CC-CLI-002] descriptor-relative create and lock remain in pinned parent after ancestor swap"
                pinnedAncestorSwap
        ]
