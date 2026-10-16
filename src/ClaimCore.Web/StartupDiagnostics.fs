namespace ClaimCore.Web

open System.Text.Json

[<RequireQualifiedAccess>]
type WebSetting =
    | ConnectionFile
    | CertificatePath
    | Origin
    | ListenAddress
    | ListenPort
    | ProbeCaCertFile
    | OidcIssuer
    | OidcClientId
    | OidcClientSecretFile
    | OidcApiAudience
    | OidcCliClientId
    | JsonLimit
    | CorePermits
    | CoreQueue
    | LoginPermits
    | SessionIdle
    | SessionAbsolute
    | OidcCaCertFile

module WebSettings =
    let private entries =
        [
            WebSetting.ConnectionFile, "CLAIMCORE_CONNECTION_FILE"
            WebSetting.CertificatePath, "CLAIMCORE_WEB_CERTIFICATE_PATH"
            WebSetting.Origin, "CLAIMCORE_WEB_ORIGIN"
            WebSetting.ListenAddress, "CLAIMCORE_WEB_LISTEN_ADDRESS"
            WebSetting.ListenPort, "CLAIMCORE_WEB_LISTEN_PORT"
            WebSetting.ProbeCaCertFile, "CLAIMCORE_WEB_PROBE_CA_CERT_FILE"
            WebSetting.OidcIssuer, "CLAIMCORE_OIDC_ISSUER"
            WebSetting.OidcClientId, "CLAIMCORE_OIDC_CLIENT_ID"
            WebSetting.OidcClientSecretFile, "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
            WebSetting.OidcApiAudience, "CLAIMCORE_OIDC_API_AUDIENCE"
            WebSetting.OidcCliClientId, "CLAIMCORE_OIDC_CLI_CLIENT_ID"
            WebSetting.JsonLimit, "CLAIMCORE_WEB_MAX_JSON_BYTES"
            WebSetting.CorePermits, "CLAIMCORE_WEB_CORE_PERMITS"
            WebSetting.CoreQueue, "CLAIMCORE_WEB_CORE_QUEUE"
            WebSetting.LoginPermits, "CLAIMCORE_WEB_LOGIN_PERMITS"
            WebSetting.SessionIdle, "CLAIMCORE_WEB_SESSION_IDLE_MINUTES"
            WebSetting.SessionAbsolute, "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES"
            WebSetting.OidcCaCertFile, "CLAIMCORE_OIDC_CA_CERT_FILE"
        ]

    let all = entries |> List.map fst

    let required =
        [
            WebSetting.ConnectionFile
            WebSetting.CertificatePath
            WebSetting.OidcIssuer
            WebSetting.OidcClientId
            WebSetting.OidcClientSecretFile
            WebSetting.OidcApiAudience
            WebSetting.OidcCliClientId
        ]

    let optional =
        all |> List.filter (fun setting -> not (List.contains setting required))

    let token setting =
        entries |> List.find (fst >> (=) setting) |> snd

[<RequireQualifiedAccess>]
type WebStartupProblem =
    | MissingSetting of WebSetting
    | InvalidSetting of WebSetting
    | OriginInvalid
    | ConnectionFileRefused
    | ConnectionFileEmpty
    | SessionLifetimeInvalid
    | OidcConfigurationInvalid
    | CertificateAccessRefused
    | CertificateInvalid
    | CertificateKeyMissing
    | AssetsMissing
    | UnsupportedInvocation
    | UnexpectedFailure
    | RuntimeOpen of string

exception WebStartupException of WebStartupProblem

module WebStartupDiagnostics =
    let refuse reason = raise (WebStartupException reason)

    let encode reason =
        let code, setting =
            match reason with
            | WebStartupProblem.MissingSetting setting ->
                "SETTING_REQUIRED", Some(WebSettings.token setting)
            | WebStartupProblem.InvalidSetting setting ->
                "SETTING_INVALID", Some(WebSettings.token setting)
            | WebStartupProblem.RuntimeOpen fault ->
                "RUNTIME_" + (string fault).ToUpperInvariant(), None
            | value -> (string value).ToUpperInvariant(), None

        JsonSerializer.SerializeToUtf8Bytes
            {|
                kind = "startupFailure"
                code = code
                setting = setting
            |}
