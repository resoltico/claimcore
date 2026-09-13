module ClaimCore.WebTests.WebHostSecurityTests

open System
open System.IO
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.PrivateTestPaths

let private ownerFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private withSandbox action =
    let directory = newPrivateDirectory "claimcore-web-private-"

    try
        action directory
    finally
        Directory.Delete(directory, true)

let private stateMode directory =
    let state = Path.Combine(directory, "state")
    Directory.CreateDirectory(state) |> ignore

    let broad =
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute
        ||| UnixFileMode.GroupRead

    File.SetUnixFileMode(state, broad)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Public existing state is refused"
    Expect.equal (File.GetUnixFileMode(state)) broad "Unsafe state mode is never rewritten"
    File.SetUnixFileMode(state, ownerFile ||| UnixFileMode.UserExecute)
    let loaded = Configuration.load ()
    use _certificate = loaded.Certificate
    state

let private stateLinks directory state =
    let linked = Path.Combine(directory, "linked-state")
    Directory.CreateSymbolicLink(linked, state) |> ignore
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_STATE_DIR", linked)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Linked state is refused"
    let ancestor = Path.Combine(directory, "linked-state-ancestor")
    Directory.CreateSymbolicLink(ancestor, directory) |> ignore

    Environment.SetEnvironmentVariable(
        "CLAIMCORE_WEB_STATE_DIR",
        Path.Combine(ancestor, "must-not-exist")
    )

    Expect.throws (fun () -> Configuration.load () |> ignore) "State ancestor link is refused"

    Expect.isFalse
        (Directory.Exists(Path.Combine(directory, "must-not-exist")))
        "No redirected state"

let private stateDirectorySafety () =
    ConfigurationTests.configured (fun directory _ _ ->
        if OperatingSystem.IsWindows() then
            Expect.throws (fun () -> Configuration.load () |> ignore) "Windows state fails closed"

            Expect.isFalse
                (Directory.Exists(Path.Combine(directory, "state")))
                "No state is created"
        else
            stateMode directory |> stateLinks directory)

let private lockBoundaries () =
    withSandbox (fun directory ->
        let lockPath = Path.Combine(directory, ".claimcore-web.lock")

        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Windows lock fails closed"

            Expect.isFalse (File.Exists(lockPath)) "Unsupported lock creates no file"
        else
            let target = Path.Combine(directory, "synthetic-target")
            File.WriteAllText(target, "synthetic")
            File.SetUnixFileMode(target, ownerFile)
            File.CreateSymbolicLink(lockPath, target) |> ignore

            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Linked lock is refused"

            Expect.isTrue (File.Exists(target)) "Linked target remains"
            File.Delete(lockPath)
            File.WriteAllText(lockPath, "synthetic")
            let broad = ownerFile ||| UnixFileMode.GroupRead
            File.SetUnixFileMode(lockPath, broad)

            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Broad lock is refused"

            Expect.equal (File.GetUnixFileMode(lockPath)) broad "Existing lock is never chmoded"
            File.SetUnixFileMode(lockPath, ownerFile)
            use _lease = Security.acquireStateDirectory directory

            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Lock excludes second host")

let private staleCredentialBoundaries () =
    withSandbox (fun directory ->
        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Security.rotateBootstrapCredential directory |> ignore)
                "Windows credential fails closed"

            Expect.equal
                (Directory.GetFiles(directory).Length)
                0
                "Unsupported rotation creates no file"
        else
            let target = Path.Combine(directory, "synthetic-target")
            File.WriteAllText(target, "synthetic")
            File.SetUnixFileMode(target, ownerFile)

            let stale =
                Path.Combine(directory, "bootstrap-credential-" + Guid.NewGuid().ToString("N"))

            File.CreateSymbolicLink(stale, target) |> ignore

            Expect.throws
                (fun () -> Security.rotateBootstrapCredential directory |> ignore)
                "Linked stale credential is refused"

            Expect.isTrue (File.Exists(target)) "Noncredential target remains"
            File.Delete(stale)
            File.WriteAllText(stale, "synthetic")
            File.SetUnixFileMode(stale, ownerFile)
            let lease = Security.rotateBootstrapCredential directory
            let current = lease.Credential.Path
            Expect.isFalse (File.Exists(stale)) "Validated stale credential is removed"
            Expect.isTrue (File.Exists(current)) "New private credential exists"

            Expect.equal
                (File.GetUnixFileMode(current))
                ownerFile
                "New credential has private mode"

            (lease :> IDisposable).Dispose()
            Expect.isFalse (File.Exists(current)) "Lease removes only its own credential")

let tests =
    testList
        "Web host private-file security"
        [
            testCase
                "[CC-WEB-001] existing state mode and links fail without chmod or redirected creation"
                stateDirectorySafety
            testCase
                "[CC-WEB-001] state lock rejects linked or broad existing files without rewriting them"
                lockBoundaries
            testCase
                "[CC-WEB-001] bootstrap rotation removes only validated private generated files"
                staleCredentialBoundaries
        ]
