module ClaimCore.WebTests.ConfigurationTests

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open ClaimCore.Web
open ClaimCore.Contracts
open ClaimCore.WebTests.PrivateTestPaths
open ClaimCore.WebTests.WebConfigurationFixture

let configured = WebConfigurationFixture.configured

let private validAndBoundedConfiguration () =
    configured (fun _ _ _ ->
        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Configuration.load () |> ignore)
                "Windows private files fail closed"
        else
            let loaded = Configuration.load ()
            use _certificate = loaded.Certificate
            Expect.equal loaded.Binding.Origin.Port 5443 "Default origin"
            Expect.equal loaded.Admission.CorePermitLimit 4 "Default permits"

        for name, value in
            [
                "CLAIMCORE_WEB_MAX_JSON_BYTES", "0"
                "CLAIMCORE_WEB_CORE_PERMITS", "5"
                "CLAIMCORE_WEB_CORE_QUEUE", "-1"
                "CLAIMCORE_WEB_LOGIN_PERMITS", "not-a-number"
            ] do
            Environment.SetEnvironmentVariable(name, value)
            Expect.throws (fun () -> Configuration.load () |> ignore) "Invalid bound"
            Environment.SetEnvironmentVariable(name, null))

let private sessionLifetimeTests () =
    configured (fun _ _ _ ->
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_SESSION_IDLE_MINUTES", "20")
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES", "10")
        Expect.throws (fun () -> Configuration.load () |> ignore) "Absolute expiry must dominate"
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES", "20")

        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Configuration.load () |> ignore)
                "Windows private files fail closed"
        else
            let loaded = Configuration.load ()
            use _certificate = loaded.Certificate
            Expect.equal loaded.SessionIdle loaded.SessionAbsolute "Equal bounds are accepted")

let private linkedAndPublicCertificateTests
    (directory: string)
    (connection: string)
    (certificatePath: string)
    =
    let connectionLink = Path.Combine(directory, "connection-link")
    File.CreateSymbolicLink(connectionLink, connection) |> ignore
    Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connectionLink)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Connection links are refused"
    Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
    let ancestor = Path.Combine(directory, "configuration-ancestor-link")
    Directory.CreateSymbolicLink(ancestor, directory) |> ignore

    Environment.SetEnvironmentVariable(
        "CLAIMCORE_CONNECTION_FILE",
        Path.Combine(ancestor, "application.connection")
    )

    Expect.throws
        (fun () -> Configuration.load () |> ignore)
        "Connection ancestor links are refused"

    Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
    let certificateLink = Path.Combine(directory, "certificate-link")
    File.CreateSymbolicLink(certificateLink, certificatePath) |> ignore
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificateLink)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Certificate links are refused"

    Environment.SetEnvironmentVariable(
        "CLAIMCORE_WEB_CERTIFICATE_PATH",
        Path.Combine(ancestor, "web.pfx")
    )

    Expect.throws
        (fun () -> Configuration.load () |> ignore)
        "Certificate ancestor links are refused"

    let publicCertificate = Path.Combine(directory, "public-only.pfx")
    certificate publicCertificate false

    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(publicCertificate, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", publicCertificate)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Private key is required"

let private permissionTests (connection: string) (certificatePath: string) =
    if not (OperatingSystem.IsWindows()) then
        let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        let publicMode = privateMode ||| UnixFileMode.GroupRead
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificatePath)
        File.SetUnixFileMode(connection, publicMode)
        Expect.throws (fun () -> Configuration.load () |> ignore) "Connection mode is private"
        File.SetUnixFileMode(connection, privateMode)
        File.SetUnixFileMode(certificatePath, publicMode)
        Expect.throws (fun () -> Configuration.load () |> ignore) "Certificate mode is private"
        File.SetUnixFileMode(certificatePath, privateMode)

let private connectionContentTests (connection: string) =
    File.WriteAllBytes(connection, [| 0xffuy |])
    Expect.throws (fun () -> Configuration.load () |> ignore) "Connection is strict UTF-8"
    File.WriteAllText(connection, String.replicate 8193 "x")
    Expect.throws (fun () -> Configuration.load () |> ignore) "Connection size is bounded"
    File.WriteAllText(connection, "Host=127.0.0.1;Database=synthetic;Username=synthetic")

let private absolutePathTests (directory: string) (connection: string) (certificatePath: string) =
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_STATE_DIR", "relative-state")
    Expect.throws (fun () -> Configuration.load () |> ignore) "State directory is absolute"
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_STATE_DIR", Path.Combine(directory, "state"))
    Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", "relative.connection")
    Expect.throws (fun () -> Configuration.load () |> ignore) "Connection path is absolute"
    Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", "relative.pfx")
    Expect.throws (fun () -> Configuration.load () |> ignore) "Certificate path is absolute"
    Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificatePath)

let private certificateIdentityTests certificatePath =
    let now = DateTimeOffset.UtcNow

    for label, name, start, finish, purpose in
        [
            "wrong host", "example.invalid", now.AddDays(-1), now.AddDays(1), "1.3.6.1.5.5.7.3.1"
            "missing DNS name", "", now.AddDays(-1), now.AddDays(1), "1.3.6.1.5.5.7.3.1"
            "expired", "localhost", now.AddDays(-3), now.AddDays(-2), "1.3.6.1.5.5.7.3.1"
            "not yet valid", "localhost", now.AddDays(1), now.AddDays(2), "1.3.6.1.5.5.7.3.1"
            "wrong purpose", "localhost", now.AddDays(-1), now.AddDays(1), "1.3.6.1.5.5.7.3.2"
        ] do
        certificateFor certificatePath true name start finish purpose

        let refused =
            try
                let loaded = Configuration.load ()
                loaded.Certificate.Dispose()
                false
            with WebStartupException WebStartupProblem.CertificateInvalid ->
                true

        Expect.isTrue refused ("Unusable localhost certificate is refused: " + label)

    certificate certificatePath true

let private unsafePathTests () =
    configured (fun directory connection certificatePath ->
        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Configuration.load () |> ignore)
                "Windows private files fail closed"

            Expect.isFalse
                (Directory.Exists(Path.Combine(directory, "state")))
                "No unsupported state"
        else
            linkedAndPublicCertificateTests directory connection certificatePath
            Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificatePath)
            permissionTests connection certificatePath
            connectionContentTests connection
            absolutePathTests directory connection certificatePath
            certificateIdentityTests certificatePath)

let tests =
    testList
        "Web configuration boundaries"
        [
            testCase
                "[CC-WEB-001] loads defaults and rejects every out-of-range admission limit"
                validAndBoundedConfiguration
            testCase "[CC-WEB-001] enforces session lifetime ordering" sessionLifetimeTests
            testCase
                "[CC-WEB-001] rejects linked credentials and certificates without private keys"
                unsafePathTests
        ]
    |> testSequenced
