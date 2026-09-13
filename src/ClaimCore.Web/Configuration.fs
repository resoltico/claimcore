namespace ClaimCore.Web

open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Globalization
open ClaimCore.Application
open ClaimCore.HostSecurity

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

    let private required name =
        environment name
        |> Option.defaultWith (fun () -> invalidOp $"Set {name} to a private configuration value.")

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
                invalidOp "CLAIMCORE_WEB_ORIGIN must be one HTTPS localhost origin without a path."

            parsed
        with :? UriFormatException ->
            invalidOp "CLAIMCORE_WEB_ORIGIN must be one HTTPS localhost origin without a path."

    let private origin () =
        environment "CLAIMCORE_WEB_ORIGIN"
        |> Option.defaultValue "https://localhost:5443"
        |> parseOrigin

    let private boundedInt name fallback maximum =
        match environment name with
        | None -> fallback
        | Some raw ->
            match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, value when value >= 1 && value <= maximum -> value
            | _ -> invalidOp $"{name} must be a positive integer no greater than {maximum}."

    let private admission () =
        {
            MaximumJsonBytes =
                boundedInt
                    "CLAIMCORE_WEB_MAX_JSON_BYTES"
                    SemanticContract.current.RequestByteLimit
                    SemanticContract.current.RequestByteLimit
            CorePermitLimit = boundedInt "CLAIMCORE_WEB_CORE_PERMITS" 4 4
            CoreQueueLimit = boundedInt "CLAIMCORE_WEB_CORE_QUEUE" 16 16
            LoginPermitLimit = boundedInt "CLAIMCORE_WEB_LOGIN_PERMITS" 5 5
        }

    let private minutes name fallback maximum =
        boundedInt name fallback maximum |> float |> TimeSpan.FromMinutes

    let private stateDirectory () =
        let path = required "CLAIMCORE_WEB_STATE_DIR"

        match PrivateFileService.ensureDirectory path with
        | Ok privatePath -> privatePath
        | Error _ ->
            invalidOp
                "CLAIMCORE_WEB_STATE_DIR must name an owner-only absolute directory without links."

    let private certificate () =
        let path = required "CLAIMCORE_WEB_CERTIFICATE_PATH"

        let bytes =
            match PrivateFileService.readBinary (8 * 1024 * 1024) path with
            | Ok value when value.Length > 0 -> value
            | Ok _
            | Error _ ->
                invalidOp "CLAIMCORE_WEB_CERTIFICATE_PATH must name a bounded private PKCS#12 file."

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
                    invalidOp "The configured Web certificate is not a valid PKCS#12 value."
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))

        if not loaded.HasPrivateKey then
            loaded.Dispose()
            invalidOp "The configured Web certificate must include a private key."

        loaded

    let private connectionString () =
        let path = required "CLAIMCORE_CONNECTION_FILE"

        let value =
            match PrivateFileService.readUtf8Text 8192 path with
            | Ok text -> text.Trim()
            | Error _ ->
                invalidOp "CLAIMCORE_CONNECTION_FILE must name a bounded private UTF-8 file."

        if String.IsNullOrWhiteSpace(value) then
            invalidOp "The configured application connection file is empty."

        value

    let load () =
        let sessionIdle = minutes "CLAIMCORE_WEB_SESSION_IDLE_MINUTES" 30 30
        let sessionAbsolute = minutes "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES" 480 480

        if sessionAbsolute < sessionIdle then
            invalidOp
                "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES must not be less than the idle lifetime."

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
