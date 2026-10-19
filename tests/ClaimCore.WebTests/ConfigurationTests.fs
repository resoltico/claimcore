module ClaimCore.WebTests.ConfigurationTests

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.PrivateTestPaths

let private configured action =
    let names = WebSettings.all |> List.map WebSettings.token

    let previous =
        names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

    let directory = newPrivateDirectory "claimcore-web-config-"

    try
        for name in names do
            Environment.SetEnvironmentVariable(name, null)

        let connection = Path.Combine(directory, "connection")
        File.WriteAllText(connection, "synthetic configuration is never opened")
        let certificate = Path.Combine(directory, "server.pfx")
        use key = RSA.Create 2048

        let request =
            CertificateRequest(
                "CN=probe.localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            )

        let names = SubjectAlternativeNameBuilder()
        names.AddDnsName "probe.localhost"
        request.CertificateExtensions.Add(names.Build())
        let usages = OidCollection()
        usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
        request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, false))

        use issued =
            request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddHours(-1.),
                DateTimeOffset.UtcNow.AddHours 1.
            )

        File.WriteAllBytes(certificate, issued.Export X509ContentType.Pfx)

        for path in [ connection; certificate ] do
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificate)
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_ORIGIN", "https://probe.localhost:5443")
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_KEY_RING_DIRECTORY", directory)
        action directory connection
    finally
        for name, value in previous do
            Environment.SetEnvironmentVariable(name, value)

        Directory.Delete(directory, true)

let private catalogue () =
    let expected =
        set
            [
                "CLAIMCORE_CONNECTION_FILE"
                "CLAIMCORE_WEB_ORIGIN"
                "CLAIMCORE_WEB_LISTEN_ADDRESS"
                "CLAIMCORE_WEB_LISTEN_PORT"
                "CLAIMCORE_WEB_KEY_RING_DIRECTORY"
                "CLAIMCORE_WEB_CERTIFICATE_PATH"
                "CLAIMCORE_WEB_SESSION_IDLE_MINUTES"
                "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES"
                "CLAIMCORE_WEB_CORE_PERMITS"
                "CLAIMCORE_WEB_CORE_QUEUE"
                "CLAIMCORE_WEB_LOGIN_PERMITS"
                "CLAIMCORE_WEB_MAX_JSON_BYTES"
                "CLAIMCORE_OIDC_ISSUER"
                "CLAIMCORE_OIDC_CLIENT_ID"
                "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
                "CLAIMCORE_OIDC_API_AUDIENCE"
                "CLAIMCORE_OIDC_CLI_CLIENT_ID"
                "CLAIMCORE_OIDC_CA_CERT_FILE"
                "CLAIMCORE_WEB_PROBE_CA_CERT_FILE"
            ]

    Expect.equal
        (WebSettings.all |> List.map WebSettings.token |> Set.ofList)
        expected
        "Exactly nineteen names"

    Expect.equal WebSettings.all.Length 19 "No duplicate registry entries"

let private limits () =
    configured (fun _ _ ->
        let value = Configuration.load ()
        use _certificates = Configuration.ownCertificates value
        Expect.equal value.Admission.MaximumJsonBytes 65536 "Default bound"
        Expect.equal value.Admission.CorePermitLimit 4 "Default permits"

        for name, value in
            [
                "CLAIMCORE_WEB_MAX_JSON_BYTES", "0"
                "CLAIMCORE_WEB_CORE_PERMITS", "5"
                "CLAIMCORE_WEB_CORE_QUEUE", "-1"
                "CLAIMCORE_WEB_LOGIN_PERMITS", "nonnumeric"
            ] do
            Environment.SetEnvironmentVariable(name, value)
            Expect.throws (fun () -> Configuration.load () |> ignore) "Invalid bounds fail closed"
            Environment.SetEnvironmentVariable(name, null))

let private privateConnection () =
    configured (fun _ connection ->
        File.SetUnixFileMode(connection, UnixFileMode.UserRead ||| UnixFileMode.GroupWrite)
        Expect.throws (fun () -> Configuration.load () |> ignore) "Group write refused"
        File.SetUnixFileMode(connection, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        File.WriteAllBytes(connection, [| 0xffuy |])
        Expect.throws (fun () -> Configuration.load () |> ignore) "Malformed UTF-8 refused")

let private keyRing () =
    configured (fun directory _ ->
        for value in [ "relative"; Path.Combine(directory, "missing") ] do
            Environment.SetEnvironmentVariable("CLAIMCORE_WEB_KEY_RING_DIRECTORY", value)

            Expect.throws
                (fun () -> Configuration.load () |> ignore)
                "Existing absolute directory required"

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_KEY_RING_DIRECTORY", directory)

        for bit in [ UnixFileMode.GroupWrite; UnixFileMode.OtherWrite ] do
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
                ||| bit
            )

            Expect.throws (fun () -> Configuration.load () |> ignore) "Shared write refuses"

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_KEY_RING_DIRECTORY", null)
        Expect.throws (fun () -> Configuration.load () |> ignore) "Required setting")

[<Tests>]
let tests =
    testList
        "host configuration"
        [
            testCase "key ring requires an existing protected directory" keyRing
            testCase "the service setting registry has exactly nineteen names" catalogue
            testCase "defaults are bounded and out-of-range settings are refused" limits
            testCase "the connection input is owner-private and strict UTF-8" privateConnection
        ]
    |> testSequenced
