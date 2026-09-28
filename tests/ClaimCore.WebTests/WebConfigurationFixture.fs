module internal ClaimCore.WebTests.WebConfigurationFixture

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.WebTests.PrivateTestPaths

let private variables =
    [
        "CLAIMCORE_WEB_ORIGIN"
        "CLAIMCORE_WEB_STATE_DIR"
        "CLAIMCORE_WEB_CERTIFICATE_PATH"
        "CLAIMCORE_CONNECTION_FILE"
        "CLAIMCORE_WITNESS_CONNECTION_FILE"
        "CLAIMCORE_WITNESS_KEY_FILE"
        "CLAIMCORE_SUPPRESSION_KEY_FILE"
        "CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE"
        "CLAIMCORE_WEB_MAX_JSON_BYTES"
        "CLAIMCORE_WEB_CORE_PERMITS"
        "CLAIMCORE_WEB_CORE_QUEUE"
        "CLAIMCORE_WEB_LOGIN_PERMITS"
        "CLAIMCORE_WEB_SESSION_IDLE_MINUTES"
        "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES"
        "CLAIMCORE_OIDC_ISSUER"
        "CLAIMCORE_OIDC_CA_CERT_FILE"
        "CLAIMCORE_OIDC_CLIENT_ID"
        "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
        "CLAIMCORE_OIDC_API_AUDIENCE"
        "CLAIMCORE_OIDC_SERVICE_CLIENT_ID"
        "CLAIMCORE_OIDC_CLI_CLIENT_ID"
    ]

let certificateFor path includeKey dnsName notBefore notAfter (purpose: string) =
    use rsa = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    let basic = X509BasicConstraintsExtension(false, false, 0, false)
    request.CertificateExtensions.Add(basic)

    if not (String.IsNullOrEmpty(dnsName)) then
        let names = SubjectAlternativeNameBuilder()
        names.AddDnsName(dnsName)
        request.CertificateExtensions.Add(names.Build())

    let usages = OidCollection()
    usages.Add(Oid(purpose)) |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, false))

    use issued = request.CreateSelfSigned(notBefore, notAfter)

    let bytes =
        if includeKey then
            issued.Export(X509ContentType.Pfx)
        else
            use publicOnly =
                X509CertificateLoader.LoadCertificate(issued.Export(X509ContentType.Cert))

            publicOnly.Export(X509ContentType.Pfx)

    File.WriteAllBytes(path, bytes)

let certificate path includeKey =
    certificateFor
        path
        includeKey
        "localhost"
        (DateTimeOffset.UtcNow.AddDays(-1))
        (DateTimeOffset.UtcNow.AddDays(1))
        "1.3.6.1.5.5.7.3.1"

let private restore originals =
    originals
    |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

let private prepareFiles directory =
    let state = Path.Combine(directory, "state")
    let connection = Path.Combine(directory, "application.connection")
    let witnessConnection = Path.Combine(directory, "witness.connection")
    let witnessKeyPath = Path.Combine(directory, "witness.key")
    let suppressionKeyPath = Path.Combine(directory, "suppression.key")
    let artifactKeyPath = Path.Combine(directory, "artifact.key")
    let certificatePath = Path.Combine(directory, "web.pfx")
    File.WriteAllText(connection, "Host=127.0.0.1;Database=synthetic;Username=synthetic")

    File.WriteAllText(
        witnessConnection,
        "Host=127.0.0.1;Database=synthetic_witness;Username=synthetic_witness"
    )

    let syntheticKey = RandomNumberGenerator.GetBytes(32)
    let syntheticKeyId = Guid.NewGuid()

    File.WriteAllText(
        witnessKeyPath,
        $"{{\"version\":1,\"activeKeyId\":\"{syntheticKeyId:D}\",\"keys\":[{{\"id\":\"{syntheticKeyId:D}\",\"materialBase64\":\"{Convert.ToBase64String(syntheticKey)}\"}}]}}"
    )

    CryptographicOperations.ZeroMemory(syntheticKey)
    let suppressionKey = RandomNumberGenerator.GetBytes(32)

    File.WriteAllText(
        suppressionKeyPath,
        $"{{\"version\":1,\"keyId\":\"{Guid.NewGuid():D}\",\"materialBase64\":\"{Convert.ToBase64String(suppressionKey)}\"}}"
    )

    CryptographicOperations.ZeroMemory(suppressionKey)

    File.WriteAllText(
        artifactKeyPath,
        "synthetic configuration path; contents are opened by Hosting"
    )

    certificate certificatePath true

    if not (OperatingSystem.IsWindows()) then
        let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        File.SetUnixFileMode(connection, privateMode)
        File.SetUnixFileMode(witnessConnection, privateMode)
        File.SetUnixFileMode(witnessKeyPath, privateMode)
        File.SetUnixFileMode(suppressionKeyPath, privateMode)
        File.SetUnixFileMode(artifactKeyPath, privateMode)
        File.SetUnixFileMode(certificatePath, privateMode)

    state,
    connection,
    witnessConnection,
    witnessKeyPath,
    suppressionKeyPath,
    artifactKeyPath,
    certificatePath

let configured action =
    let originals =
        variables
        |> List.map (fun name -> name, Environment.GetEnvironmentVariable(name))

    let directory = newPrivateDirectory "claimcore-web-config-"

    let (state,
         connection,
         witnessConnection,
         witnessKeyPath,
         suppressionKeyPath,
         artifactKeyPath,
         certificatePath) =
        prepareFiles directory

    try
        variables
        |> List.iter (fun name -> Environment.SetEnvironmentVariable(name, null))

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_STATE_DIR", state)
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_CERTIFICATE_PATH", certificatePath)
        Environment.SetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE", connection)
        Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_CONNECTION_FILE", witnessConnection)
        Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_KEY_FILE", witnessKeyPath)
        Environment.SetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE", suppressionKeyPath)
        Environment.SetEnvironmentVariable("CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE", artifactKeyPath)
        action directory connection certificatePath
    finally
        restore originals
        Directory.Delete(directory, true)
