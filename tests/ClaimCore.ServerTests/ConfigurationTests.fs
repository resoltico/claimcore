module ClaimCore.ServerTests.ConfigurationTests

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open Expecto
open ClaimCore.Server
open ClaimCore.TestSupport.PrivateTestPaths

let private configured action =
    let names = WebSettings.all |> List.map WebSettings.token

    let previous =
        names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

    let directory = newPrivateDirectory "claimcore-web-config-"

    try
        for name in names do
            Environment.SetEnvironmentVariable(name, null)

        let connection = Path.Combine(directory, "connection")
        File.WriteAllText(connection, "Host=localhost;Maximum Pool Size=15")
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

        let keyFile = Path.Combine(directory, "data.key")
        File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes 32)
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        Environment.SetEnvironmentVariable("CLAIMCORE_DATA_KEY_FILE", keyFile)
        Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
        Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_CONNECTION_FILE", connection)
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
                "CLAIMCORE_WITNESS_CONNECTION_FILE"
                "CLAIMCORE_DATA_KEY_FILE"
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
                "CLAIMCORE_DIGEST_SMTP_HOST"
                "CLAIMCORE_DIGEST_SMTP_PORT"
                "CLAIMCORE_DIGEST_SMTP_TLS"
                "CLAIMCORE_DIGEST_SMTP_CREDENTIALS_FILE"
                "CLAIMCORE_DIGEST_SMTP_CA_CERT_FILE"
                "CLAIMCORE_DIGEST_FROM"
                "CLAIMCORE_DIGEST_TIME"
                "CLAIMCORE_DIGEST_LANGUAGE"
            ]

    Expect.equal
        (WebSettings.all |> List.map WebSettings.token |> Set.ofList)
        expected
        "Exactly the documented setting names"

    Expect.equal WebSettings.all.Length expected.Count "No duplicate registry entries"

let private limits () =
    configured (fun _ _ ->
        let value = Configuration.load ()
        use _certificates = Configuration.ownCertificates value
        Expect.equal value.RequestGate.MaximumJsonBytes 65536 "Default bound"
        Expect.equal value.RequestGate.CorePermitLimit 4 "Default permits"

        Expect.equal
            value.RequestGate.CoreQueueLimit
            8
            "Queued writers leave ingress capacity for other work"

        for name, value in
            [
                "CLAIMCORE_WEB_MAX_JSON_BYTES", "0"
                "CLAIMCORE_WEB_CORE_PERMITS", "5"
                "CLAIMCORE_WEB_CORE_QUEUE", "-1"
                "CLAIMCORE_WEB_CORE_QUEUE", "9"
                "CLAIMCORE_WEB_LOGIN_PERMITS", "nonnumeric"
            ] do
            Environment.SetEnvironmentVariable(name, value)
            Expect.throws (fun () -> Configuration.load () |> ignore) "Invalid bounds fail closed"
            Environment.SetEnvironmentVariable(name, null))

let private poolCapacity () =
    configured (fun _ connection ->
        for settings in [ "Host=localhost;Maximum Pool Size=14"; "Host=localhost;Pooling=false" ] do
            File.WriteAllText(connection, settings)
            let mutable refused = false

            try
                let configured = Configuration.load ()
                use _certificates = Configuration.ownCertificates configured
                ()
            with WebStartupException WebStartupProblem.PoolCapacityInsufficient ->
                refused <- true

            Expect.isTrue
                refused
                "The default resource budgets require a real pool with fifteen connections, including housekeeping"

        File.WriteAllText(connection, "Host=localhost;Maximum Pool Size=15")
        let configured = Configuration.load ()
        use _certificates = Configuration.ownCertificates configured
        ())

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

let private digestSettings =
    [
        "CLAIMCORE_DIGEST_SMTP_HOST", "mail.example.test"
        "CLAIMCORE_DIGEST_SMTP_PORT", "587"
        "CLAIMCORE_DIGEST_SMTP_TLS", "STARTTLS"
        "CLAIMCORE_DIGEST_FROM", "sender@example.test"
        "CLAIMCORE_DIGEST_TIME", "07:00"
    ]

let private expectDigestFault code setting =
    try
        let configuration = Configuration.load ()
        use _certificates = Configuration.ownCertificates configuration
        failtestf "Expected startup to refuse %s." setting
    with WebStartupException problem ->
        use failure = JsonDocument.Parse((WebStartupDiagnostics.encode problem).AsMemory())

        Expect.equal (failure.RootElement.GetProperty("code").GetString()) code "Closed fault code"

        Expect.equal
            (failure.RootElement.GetProperty("setting").GetString())
            setting
            "Reports the setting name"

        Expect.equal
            (failure.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            (set [ "kind"; "code"; "setting" ])
            "The fault contains no submitted inputs"

let private digestAvailability () =
    configured (fun _ connection ->
        File.WriteAllText(connection, "Host=localhost;Maximum Pool Size=16")
        let withoutSettings = Configuration.load ()
        use _withoutCertificates = Configuration.ownCertificates withoutSettings
        Expect.isNone withoutSettings.Digest "Absent settings disable digest"

        for setting, value in digestSettings do
            Environment.SetEnvironmentVariable(setting, value)

        let configuredDigest = Configuration.load ()
        use _configuredCertificates = Configuration.ownCertificates configuredDigest

        let digest =
            Expect.wantSome configuredDigest.Digest "Complete settings enable digest"

        Expect.equal digest.Language "en" "Default language"
        Expect.equal digest.Time (TimeOnly(7, 0)) "Business time of day"
        Expect.equal digest.Smtp.Tls DigestSmtpTls.StartTls "Mandatory STARTTLS"
        Expect.equal digest.Smtp.Port 587 "Configured SMTP port"
        Expect.isNone digest.Smtp.Credentials "Authentication is optional"
        Expect.isNone digest.Smtp.TrustRoot "Default platform trust"

        for setting, value in digestSettings do
            Environment.SetEnvironmentVariable(setting, null)
            expectDigestFault "SETTING_REQUIRED" setting
            Environment.SetEnvironmentVariable(setting, value)

        for setting, value in
            [
                "CLAIMCORE_DIGEST_SMTP_HOST", "smtp://mail.example.test"
                "CLAIMCORE_DIGEST_SMTP_PORT", "65536"
                "CLAIMCORE_DIGEST_SMTP_TLS", "PLAINTEXT"
                "CLAIMCORE_DIGEST_FROM", "Sender <sender@example.test>"
                "CLAIMCORE_DIGEST_TIME", "24:00"
                "CLAIMCORE_DIGEST_TIME", "7:00"
                "CLAIMCORE_DIGEST_LANGUAGE", "en-XA"
            ] do
            let previous = Environment.GetEnvironmentVariable setting
            Environment.SetEnvironmentVariable(setting, value)
            expectDigestFault "SETTING_INVALID" setting
            Environment.SetEnvironmentVariable(setting, previous)

        Environment.SetEnvironmentVariable("CLAIMCORE_DIGEST_LANGUAGE", "lv")
        Environment.SetEnvironmentVariable("CLAIMCORE_DIGEST_SMTP_TLS", "IMPLICIT")
        let localized = Configuration.load ()
        use _localizedCertificates = Configuration.ownCertificates localized
        let digest = Expect.wantSome localized.Digest "Offered language and implicit TLS"
        Expect.equal digest.Language "lv" "Configured language"
        Expect.equal digest.Smtp.Tls DigestSmtpTls.Implicit "Implicit TLS")

let private digestPrivateFiles () =
    configured (fun directory connection ->
        File.WriteAllText(connection, "Host=localhost;Maximum Pool Size=16")

        for setting, value in digestSettings do
            Environment.SetEnvironmentVariable(setting, value)

        let setting = "CLAIMCORE_DIGEST_SMTP_CREDENTIALS_FILE"
        let credentials = Path.Combine(directory, "credentials")
        File.WriteAllText(credentials, "synthetic-user\r\n synthetic-password \r\n")
        Environment.SetEnvironmentVariable(setting, credentials)
        File.SetUnixFileMode(credentials, UnixFileMode.UserRead ||| UnixFileMode.GroupWrite)
        expectDigestFault "SETTING_INVALID" setting

        File.SetUnixFileMode(credentials, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let configured = Configuration.load ()
        use _certificates = Configuration.ownCertificates configured

        let digest = Expect.wantSome configured.Digest "Digest is enabled"

        Expect.equal
            digest.Smtp.Credentials
            (Some("synthetic-user", " synthetic-password "))
            "Reads the two private lines and preserves the password"

        File.AppendAllText(credentials, "unexpected third line")
        expectDigestFault "SETTING_INVALID" setting

        Environment.SetEnvironmentVariable(setting, null)
        let setting = "CLAIMCORE_DIGEST_SMTP_CA_CERT_FILE"
        let ca = Path.Combine(directory, "ca.pem")
        File.WriteAllText(ca, "not a certificate")
        File.SetUnixFileMode(ca, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        Environment.SetEnvironmentVariable(setting, ca)
        expectDigestFault "SETTING_INVALID" setting)

[<Tests>]
let tests =
    testList
        "host configuration"
        [
            testCase "key ring requires an existing protected directory" keyRing
            testCase "the service setting registry matches the configuration contract" catalogue
            testCase "defaults are bounded and out-of-range settings are refused" limits
            testCase "the connection input is owner-private and strict UTF-8" privateConnection
            testCase
                "digest settings are optional as a group and validated at startup"
                digestAvailability
            testCase "digest credential and trust files use private admission" digestPrivateFiles
            testCase "provider capacity must cover the concurrent workload budgets" poolCapacity
        ]
    |> testSequenced
