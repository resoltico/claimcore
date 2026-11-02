module internal ClaimCore.AcceptanceTests.FixtureFiles

open System
open System.IO
open System.Diagnostics
open System.Globalization
open System.Text
open Expecto
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates

let directory () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let path =
        Path.Combine(physical, "claimcore-acceptance-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    path

let write directory name (bytes: byte array) =
    let path = Path.Combine(directory, name)
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path

let text directory name (value: string) =
    write directory name (Text.Encoding.UTF8.GetBytes value)

let private ephemeralRanges =
    lazy
        let text, expected =
            if OperatingSystem.IsLinux() then
                File.ReadAllText "/proc/sys/net/ipv4/ip_local_port_range", 2
            elif OperatingSystem.IsMacOS() then
                let start =
                    ProcessStartInfo(
                        "/usr/sbin/sysctl",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    )

                for argument in
                    [
                        "-n"
                        "net.inet.ip.portrange.first"
                        "net.inet.ip.portrange.last"
                        "net.inet.ip.portrange.hifirst"
                        "net.inet.ip.portrange.hilast"
                    ] do
                    start.ArgumentList.Add argument

                let result = ClaimCore.TestSupport.BoundedProcess.run start None 1024 5000

                if result.ExitCode <> 0 then
                    invalidOp "Cannot read acceptance host ephemeral port ranges."

                Encoding.UTF8.GetString result.StandardOutput, 4
            else
                invalidOp "Acceptance fixture ports require Linux or macOS."

        let values =
            text.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun value -> Int32.Parse(value, CultureInfo.InvariantCulture))

        if values.Length <> expected then
            invalidOp "Unexpected acceptance host ephemeral port ranges."

        values
        |> Array.chunkBySize 2
        |> Array.map (fun pair ->
            let first, last = pair[0], pair[1]

            if first < 1 || last > 65535 || first > last then
                invalidOp "Invalid acceptance host ephemeral port range."

            first, last)

let private portGate = obj ()
let mutable private nextPort = 10000

// Keep service listeners out of kernel/Docker dynamic allocation and never reuse a port in this process.
let port () =
    lock portGate (fun () ->
        let mutable selected = None

        while selected.IsNone && nextPort < 32768 do
            let candidate = nextPort
            nextPort <- nextPort + 1

            if
                ephemeralRanges.Value
                |> Array.forall (fun (first, last) -> candidate < first || candidate > last)
            then
                try
                    use listener = new TcpListener(IPAddress.Loopback, candidate)
                    listener.ExclusiveAddressUse <- true
                    listener.Start()
                    selected <- Some candidate
                with :? SocketException as error when
                    error.SocketErrorCode = SocketError.AddressAlreadyInUse ->
                    ()

        selected
        |> Option.defaultWith (fun () ->
            invalidOp "No acceptance service port outside the ephemeral ranges is available."))

[<Tests>]
let portTests =
    testCase "owned service ports avoid dynamic allocation and concurrent fixture reuse" (fun () ->
        let allocated = [| 1..32 |] |> Array.Parallel.map (fun _ -> port ())

        Expect.equal
            (Set.ofArray allocated |> Set.count)
            allocated.Length
            "Concurrent fixtures receive distinct ports"

        for value in allocated do
            Expect.isTrue
                (value >= 10000 && value < 32768)
                "Service ports exclude the container dynamic range"

            use listener = new TcpListener(IPAddress.Loopback, value)
            listener.ExclusiveAddressUse <- true
            listener.Start())

let certificate () =
    use key = RSA.Create(2048)

    let request =
        // Distinguish the issuer from its localhost leaves for OpenSSL chain construction.
        CertificateRequest(
            "CN=ClaimCore synthetic service root",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.DigitalSignature,
            true
        )
    )

    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))
    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    request.CertificateExtensions.Add(names.Build())
    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1.), DateTimeOffset.UtcNow.AddDays(1.))

/// A replacement leaf preserves the owned trust anchor and localhost identity.
let serverCertificate (issuer: X509Certificate2) =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
    )

    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))
    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    request.CertificateExtensions.Add(names.Build())

    use publicCertificate =
        request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1.),
            DateTimeOffset.UtcNow.AddHours(12.),
            RandomNumberGenerator.GetBytes 16
        )

    publicCertificate.CopyWithPrivateKey key
