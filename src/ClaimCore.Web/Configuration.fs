namespace ClaimCore.Web

open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Globalization
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type WebAdmissionLimits =
    {
        MaximumJsonBytes: int
        CorePermitLimit: int
        CoreQueueLimit: int
        LoginPermitLimit: int
    }

[<NoEquality; NoComparison>]
type WebConfiguration =
    {
        Origin: Uri
        ConnectionString: string
        StateDirectory: string
        Certificate: X509Certificate2
        SessionIdle: TimeSpan
        SessionAbsolute: TimeSpan
        Admission: WebAdmissionLimits
    }

module Configuration =
    let private environment name =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private required setting =
        environment (WebSettings.token setting)
        |> Option.defaultWith (fun () ->
            WebStartupDiagnostics.refuse (WebStartupProblem.MissingSetting setting))

    let parseOrigin value =
        try
            let parsed = Uri(value, UriKind.Absolute)

            if
                parsed.Scheme <> Uri.UriSchemeHttps
                || not (parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                || parsed.PathAndQuery <> "/"
                || not (String.IsNullOrEmpty(parsed.Fragment))
                || not (String.IsNullOrEmpty(parsed.UserInfo))
            then
                WebStartupDiagnostics.refuse WebStartupProblem.OriginInvalid

            parsed
        with :? UriFormatException ->
            WebStartupDiagnostics.refuse WebStartupProblem.OriginInvalid

    let private origin () =
        environment "CLAIMCORE_WEB_ORIGIN"
        |> Option.defaultValue "https://localhost:5443"
        |> parseOrigin

    let private boundedInt setting fallback maximum =
        match environment (WebSettings.token setting) with
        | None -> fallback
        | Some raw ->
            match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, value when value >= 1 && value <= maximum -> value
            | _ -> WebStartupDiagnostics.refuse (WebStartupProblem.InvalidSetting setting)

    let private admission () =
        {
            MaximumJsonBytes =
                boundedInt
                    WebSetting.JsonLimit
                    SemanticContract.current.RequestByteLimit
                    SemanticContract.current.RequestByteLimit
            CorePermitLimit = boundedInt WebSetting.CorePermits 4 4
            CoreQueueLimit = boundedInt WebSetting.CoreQueue 16 16
            LoginPermitLimit = boundedInt WebSetting.LoginPermits 5 5
        }

    let private minutes name fallback maximum =
        boundedInt name fallback maximum |> float |> TimeSpan.FromMinutes

    let private stateDirectory () =
        let path = required WebSetting.StateDirectory

        match PrivateFileService.ensureDirectory path with
        | Ok privatePath -> privatePath
        | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.StateDirectoryRefused

    let private certificate () =
        let path = required WebSetting.CertificatePath

        let bytes =
            match PrivateFileService.readBinary (8 * 1024 * 1024) path with
            | Ok value when value.Length > 0 -> value
            | Ok _
            | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.CertificateAccessRefused

        // macOS does not implement EphemeralKeySet for PKCS#12 imports. The certificate is a
        // private, mode-restricted local deployment input; use the platform default there and
        // retain ephemeral key storage everywhere it is supported.
        let keyStorage =
            if OperatingSystem.IsMacOS() then
                X509KeyStorageFlags.DefaultKeySet
            else
                X509KeyStorageFlags.EphemeralKeySet

        let loaded =
            try
                try
                    X509CertificateLoader.LoadPkcs12(
                        ReadOnlySpan<byte>(bytes),
                        ReadOnlySpan<char>.Empty,
                        keyStorage
                    )
                with :? CryptographicException ->
                    WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))

        if not loaded.HasPrivateKey then
            loaded.Dispose()
            WebStartupDiagnostics.refuse WebStartupProblem.CertificateKeyMissing

        let usable =
            try
                let now = DateTime.UtcNow

                let serverAuthentication =
                    loaded.Extensions
                    |> Seq.tryPick (function
                        | :? X509EnhancedKeyUsageExtension as value -> Some value
                        | _ -> None)
                    |> Option.exists (fun value ->
                        value.EnhancedKeyUsages
                        |> Seq.cast<Oid>
                        |> Seq.exists (fun usage -> usage.Value = "1.3.6.1.5.5.7.3.1"))

                loaded.NotBefore.ToUniversalTime() <= now
                && now < loaded.NotAfter.ToUniversalTime()
                && loaded.MatchesHostname("localhost", false, false)
                && serverAuthentication
            with :? CryptographicException ->
                false

        if not usable then
            loaded.Dispose()
            WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid

        loaded

    let private connectionString () =
        let path = required WebSetting.ConnectionFile

        let value =
            match PrivateFileService.readUtf8Text 8192 path with
            | Ok text -> text.Trim()
            | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.ConnectionFileRefused

        if String.IsNullOrWhiteSpace(value) then
            WebStartupDiagnostics.refuse WebStartupProblem.ConnectionFileEmpty

        value

    let load () =
        let sessionIdle = minutes WebSetting.SessionIdle 30 30
        let sessionAbsolute = minutes WebSetting.SessionAbsolute 480 480

        if sessionAbsolute < sessionIdle then
            WebStartupDiagnostics.refuse WebStartupProblem.SessionLifetimeInvalid

        let configuredOrigin = origin ()
        let configuredConnection = connectionString ()
        let configuredState = stateDirectory ()
        let configuredAdmission = admission ()
        // Load the private key only after every other fallible setting has been validated.
        let configuredCertificate = certificate ()

        {
            Origin = configuredOrigin
            ConnectionString = configuredConnection
            StateDirectory = configuredState
            Certificate = configuredCertificate
            SessionIdle = sessionIdle
            SessionAbsolute = sessionAbsolute
            Admission = configuredAdmission
        }
