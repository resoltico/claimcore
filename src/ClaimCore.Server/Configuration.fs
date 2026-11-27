namespace ClaimCore.Server

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Globalization
open ClaimCore.HostSecurity
open ClaimCore.Server.Composition

[<NoEquality; NoComparison>]
type RequestGateLimits =
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
        CliClientId: string
        ServiceClientIds: Set<string>
    }

[<NoEquality; NoComparison>]
type WebConfiguration =
    {
        KeyRingDirectory: DirectoryInfo
        Binding: WebBinding
        ConnectionString: string
        WitnessConnectionString: string
        DataKeyFile: string
        Certificate: ServerCertificateBundle
        SessionIdle: TimeSpan
        SessionAbsolute: TimeSpan
        RequestGate: RequestGateLimits
        Oidc: OidcConfiguration option
        Digest: DigestConfiguration option
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

    let parseOrigin = WebBindings.parseOrigin

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

    let private requestLimits () =
        {
            MaximumJsonBytes =
                boundedInt
                    WebSetting.JsonLimit
                    Limits.MaximumRequestBytes
                    Limits.MaximumRequestBytes
            CorePermitLimit =
                boundedInt
                    WebSetting.CorePermits
                    AdmissionPolicy.maximumCorePermits
                    AdmissionPolicy.maximumCorePermits
            CoreQueueLimit =
                boundedInt
                    WebSetting.CoreQueue
                    AdmissionPolicy.maximumQueuedWriters
                    AdmissionPolicy.maximumQueuedWriters
            LoginPermitLimit = boundedInt WebSetting.LoginPermits 5 5
        }

    let private minutes name fallback maximum =
        boundedInt name fallback maximum |> float |> TimeSpan.FromMinutes

    let private privateConnection setting refused empty =
        let path = required setting

        let value =
            match PrivateInputs.text 8192 path with
            | Ok text -> text.Trim()
            | Error _ -> WebStartupDiagnostics.refuse refused

        if String.IsNullOrWhiteSpace(value) then
            WebStartupDiagnostics.refuse empty

        value

    let private oidc () =
        match environment (WebSettings.token WebSetting.OidcIssuer) with
        | None -> None
        | Some source ->
            let invalid () =
                WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

            let issuer =
                match OidcAuthority.parse source with
                | Ok value -> value
                | Error _ -> invalid ()

            let requiredName setting =
                match environment (WebSettings.token setting) with
                | Some value when value.Length <= 256 -> value
                | _ -> invalid ()

            let secretPath = requiredName WebSetting.OidcClientSecretFile

            let clientSecret =
                match PrivateInputs.text 8192 secretPath with
                | Ok value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
                | _ -> invalid ()

            let clientId = requiredName WebSetting.OidcClientId
            let audience = requiredName WebSetting.OidcApiAudience
            let cliClient = requiredName WebSetting.OidcCliClientId

            let serviceClients =
                (requiredName WebSetting.OidcServiceClientIds).Split(',')
                |> Array.map _.Trim()
                |> Array.toList

            let all = clientId :: audience :: cliClient :: serviceClients

            if
                (all |> Set.ofList |> Set.count) <> all.Length
                || serviceClients |> List.exists String.IsNullOrWhiteSpace
            then
                invalid ()

            let trustRoot =
                match environment "CLAIMCORE_OIDC_CA_CERT_FILE" with
                | None -> None
                | Some path when HttpsOrigins.isLocal issuer ->
                    Some(PrivateTrustRoot.load WebStartupProblem.OidcConfigurationInvalid path)
                | Some _ -> invalid ()

            Some
                {
                    Issuer = issuer
                    TrustRoot = trustRoot
                    ClientId = clientId
                    ClientSecret = clientSecret
                    ApiAudience = audience
                    CliClientId = cliClient
                    ServiceClientIds = Set.ofList serviceClients
                }

    /// The startup caller owns the certificates of a successfully loaded configuration.
    let ownCertificates (configuration: WebConfiguration) =
        { new IDisposable with
            member _.Dispose() =
                try
                    configuration.Digest |> Option.iter DigestConfiguration.dispose
                finally
                    try
                        configuration.Oidc |> Option.bind _.TrustRoot |> Option.iter _.Dispose()
                    finally
                        configuration.Certificate.Dispose()
        }

    let private binding () =
        let configuredOrigin = origin ()

        WebBindings.create
            configuredOrigin
            (environment (WebSettings.token WebSetting.ListenAddress)
             |> Option.defaultValue "127.0.0.1")
            (boundedInt WebSetting.ListenPort configuredOrigin.Port 65535)

    let private sessionLifetimes () =
        let sessionIdle = minutes WebSetting.SessionIdle 30 30
        let sessionAbsolute = minutes WebSetting.SessionAbsolute 480 480

        if sessionAbsolute < sessionIdle then
            WebStartupDiagnostics.refuse WebStartupProblem.SessionLifetimeInvalid

        sessionIdle, sessionAbsolute

    let load () =
        let sessionIdle, sessionAbsolute = sessionLifetimes ()
        let configuredEndpoint = binding ()
        let keyRingDirectory = required WebSetting.KeyRingDirectory |> SessionKeys.admit

        let configuredConnection =
            privateConnection
                WebSetting.ConnectionFile
                WebStartupProblem.ConnectionFileRefused
                WebStartupProblem.ConnectionFileEmpty

        let witnessConnection =
            privateConnection
                WebSetting.WitnessConnectionFile
                (WebStartupProblem.InvalidSetting WebSetting.WitnessConnectionFile)
                (WebStartupProblem.InvalidSetting WebSetting.WitnessConnectionFile)

        let dataKeyFile = required WebSetting.DataKeyFile
        let configuredLimits = requestLimits ()

        let sufficientPool =
            try
                ClaimCore.Application.Runtime.supportsCapacity
                    (AdmissionPolicy.minimumPoolCapacity configuredLimits.CorePermitLimit)
                    configuredConnection
            with :? ArgumentException ->
                false

        if not sufficientPool then
            WebStartupDiagnostics.refuse WebStartupProblem.PoolCapacityInsufficient

        let configuredOidc = oidc ()

        try
            let configuredCertificate =
                TlsCertificate.load
                    configuredEndpoint.Origin.IdnHost
                    (required WebSetting.CertificatePath)

            let configuredDigest =
                try
                    let digest = DigestConfiguration.load ()

                    if digest.IsSome then
                        let minimum =
                            AdmissionPolicy.minimumPoolCapacity configuredLimits.CorePermitLimit
                            + AdmissionPolicy.digest

                        if
                            not (
                                ClaimCore.Application.Runtime.supportsCapacity
                                    minimum
                                    configuredConnection
                            )
                        then
                            digest |> Option.iter DigestConfiguration.dispose
                            WebStartupDiagnostics.refuse WebStartupProblem.PoolCapacityInsufficient

                    digest
                with _ ->
                    configuredCertificate.Dispose()
                    reraise ()

            {
                KeyRingDirectory = keyRingDirectory
                Binding = configuredEndpoint
                ConnectionString = configuredConnection
                WitnessConnectionString = witnessConnection
                DataKeyFile = dataKeyFile
                Certificate = configuredCertificate
                SessionIdle = sessionIdle
                SessionAbsolute = sessionAbsolute
                RequestGate = configuredLimits
                Oidc = configuredOidc
                Digest = configuredDigest
            }
        with _ ->
            configuredOidc |> Option.bind _.TrustRoot |> Option.iter _.Dispose()
            reraise ()
