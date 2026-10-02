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
type OidcConfiguration =
    {
        Issuer: Uri
        TrustRoot: X509Certificate2 option
        ClientId: string
        ClientSecret: string
        ApiAudience: string
        ServiceClientId: string
        CliClientId: string
    }

[<NoEquality; NoComparison>]
type WebConfiguration =
    {
        Origin: Uri
        ConnectionString: string
        WitnessConnectionString: string
        WitnessKeyRingPath: string
        SuppressionKeyPath: string
        RecoveryArtifactKeyPath: string
        StateDirectory: string
        Certificate: X509Certificate2
        SessionIdle: TimeSpan
        SessionAbsolute: TimeSpan
        Admission: WebAdmissionLimits
        Oidc: OidcConfiguration option
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

    let private privateConnection setting refused empty =
        let path = required setting

        let value =
            match PrivateFileService.readUtf8Text 8192 path with
            | Ok text -> text.Trim()
            | Error _ -> WebStartupDiagnostics.refuse refused

        if String.IsNullOrWhiteSpace(value) then
            WebStartupDiagnostics.refuse empty

        value

    let private oidc () =
        match environment "CLAIMCORE_OIDC_ISSUER" with
        | None -> None
        | Some source ->
            let invalid () =
                WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

            let issuer =
                match OidcAuthority.parse source with
                | Ok value -> value
                | Error _ -> invalid ()

            let requiredName name =
                match environment name with
                | Some value when value.Length <= 256 -> value
                | _ -> invalid ()

            let secretPath = requiredName "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"

            let clientSecret =
                match PrivateFileService.readUtf8Text 8192 secretPath with
                | Ok value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
                | _ -> invalid ()

            let clientId = requiredName "CLAIMCORE_OIDC_CLIENT_ID"
            let audience = requiredName "CLAIMCORE_OIDC_API_AUDIENCE"
            let serviceClient = requiredName "CLAIMCORE_OIDC_SERVICE_CLIENT_ID"
            let cliClient = requiredName "CLAIMCORE_OIDC_CLI_CLIENT_ID"

            if [ clientId; audience; serviceClient; cliClient ] |> Set.ofList |> Set.count <> 4 then
                invalid ()

            let trustRoot =
                match environment "CLAIMCORE_OIDC_CA_CERT_FILE" with
                | None -> None
                | Some path when issuer.IsLoopback -> Some(OidcTrustRoot.load path)
                | Some _ -> invalid ()

            Some
                {
                    Issuer = issuer
                    TrustRoot = trustRoot
                    ClientId = clientId
                    ClientSecret = clientSecret
                    ApiAudience = audience
                    ServiceClientId = serviceClient
                    CliClientId = cliClient
                }

    /// The startup caller owns the certificates of a successfully loaded configuration.
    let ownCertificates (configuration: WebConfiguration) =
        { new IDisposable with
            member _.Dispose() =
                try
                    configuration.Oidc |> Option.bind _.TrustRoot |> Option.iter _.Dispose()
                finally
                    configuration.Certificate.Dispose()
        }

    let private privateKeyPath = WebPrivateKeyPaths.requireAbsolute required

    let load () =
        let sessionIdle = minutes WebSetting.SessionIdle 30 30
        let sessionAbsolute = minutes WebSetting.SessionAbsolute 480 480

        if sessionAbsolute < sessionIdle then
            WebStartupDiagnostics.refuse WebStartupProblem.SessionLifetimeInvalid

        let configuredOrigin = origin ()

        let configuredConnection =
            privateConnection
                WebSetting.ConnectionFile
                WebStartupProblem.ConnectionFileRefused
                WebStartupProblem.ConnectionFileEmpty

        let configuredWitnessConnection =
            privateConnection
                WebSetting.WitnessConnectionFile
                WebStartupProblem.WitnessConnectionFileRefused
                WebStartupProblem.WitnessConnectionFileEmpty

        let configuredState = stateDirectory ()
        let configuredAdmission = admission ()
        let configuredOidc = oidc ()

        try
            let configuredWitnessKeyPath =
                privateKeyPath WebSetting.WitnessKeyFile WebStartupProblem.WitnessKeyFileRefused

            let configuredSuppressionKeyPath =
                privateKeyPath
                    WebSetting.SuppressionKeyFile
                    WebStartupProblem.SuppressionKeyFileRefused

            let configuredArtifactKeyPath =
                privateKeyPath
                    WebSetting.RecoveryArtifactKeyFile
                    WebStartupProblem.RecoveryArtifactKeyFileRefused
            // The Hosting key custodian opens and validates the private key-ring file.
            let configuredCertificate =
                TlsCertificate.load (required WebSetting.CertificatePath)

            {
                Origin = configuredOrigin
                ConnectionString = configuredConnection
                WitnessConnectionString = configuredWitnessConnection
                WitnessKeyRingPath = configuredWitnessKeyPath
                SuppressionKeyPath = configuredSuppressionKeyPath
                RecoveryArtifactKeyPath = configuredArtifactKeyPath
                StateDirectory = configuredState
                Certificate = configuredCertificate
                SessionIdle = sessionIdle
                SessionAbsolute = sessionAbsolute
                Admission = configuredAdmission
                Oidc = configuredOidc
            }
        with _ ->
            configuredOidc |> Option.bind _.TrustRoot |> Option.iter _.Dispose()
            reraise ()
