namespace ClaimCore.Server

open System.Text.Json
open ClaimCore.Application

[<RequireQualifiedAccess>]
type WebSetting =
    | WitnessConnectionFile
    | ConnectionFile
    | DataKeyFile
    | CertificatePath
    | KeyRingDirectory
    | Origin
    | ListenAddress
    | ListenPort
    | ProbeCaCertFile
    | OidcIssuer
    | OidcClientId
    | OidcClientSecretFile
    | OidcApiAudience
    | OidcCliClientId
    | OidcServiceClientIds
    | JsonLimit
    | CorePermits
    | CoreQueue
    | LoginPermits
    | SessionIdle
    | SessionAbsolute
    | OidcCaCertFile
    | DigestSmtpHost
    | DigestSmtpPort
    | DigestSmtpTls
    | DigestSmtpCredentialsFile
    | DigestSmtpCaCertFile
    | DigestFrom
    | DigestTime
    | DigestLanguage

module WebSettings =
    let private digestEntries =
        [
            WebSetting.DigestSmtpHost, "CLAIMCORE_DIGEST_SMTP_HOST"
            WebSetting.DigestSmtpPort, "CLAIMCORE_DIGEST_SMTP_PORT"
            WebSetting.DigestSmtpTls, "CLAIMCORE_DIGEST_SMTP_TLS"
            WebSetting.DigestSmtpCredentialsFile, "CLAIMCORE_DIGEST_SMTP_CREDENTIALS_FILE"
            WebSetting.DigestSmtpCaCertFile, "CLAIMCORE_DIGEST_SMTP_CA_CERT_FILE"
            WebSetting.DigestFrom, "CLAIMCORE_DIGEST_FROM"
            WebSetting.DigestTime, "CLAIMCORE_DIGEST_TIME"
            WebSetting.DigestLanguage, "CLAIMCORE_DIGEST_LANGUAGE"
        ]

    let private entries =
        [
            WebSetting.WitnessConnectionFile, "CLAIMCORE_WITNESS_CONNECTION_FILE"
            WebSetting.ConnectionFile, "CLAIMCORE_CONNECTION_FILE"
            WebSetting.DataKeyFile, MasterKeyFile.setting
            WebSetting.KeyRingDirectory, "CLAIMCORE_WEB_KEY_RING_DIRECTORY"
            WebSetting.CertificatePath, "CLAIMCORE_WEB_CERTIFICATE_PATH"
            WebSetting.Origin, "CLAIMCORE_WEB_ORIGIN"
            WebSetting.ListenAddress, "CLAIMCORE_WEB_LISTEN_ADDRESS"
            WebSetting.ListenPort, "CLAIMCORE_WEB_LISTEN_PORT"
            WebSetting.ProbeCaCertFile, "CLAIMCORE_WEB_PROBE_CA_CERT_FILE"
            WebSetting.OidcIssuer, "CLAIMCORE_OIDC_ISSUER"
            WebSetting.OidcClientId, "CLAIMCORE_OIDC_CLIENT_ID"
            WebSetting.OidcClientSecretFile, "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
            WebSetting.OidcApiAudience, "CLAIMCORE_OIDC_API_AUDIENCE"
            WebSetting.OidcServiceClientIds, "CLAIMCORE_OIDC_SERVICE_CLIENT_IDS"
            WebSetting.OidcCliClientId, "CLAIMCORE_OIDC_CLI_CLIENT_ID"
            WebSetting.JsonLimit, "CLAIMCORE_WEB_MAX_JSON_BYTES"
            WebSetting.CorePermits, "CLAIMCORE_WEB_CORE_PERMITS"
            WebSetting.CoreQueue, "CLAIMCORE_WEB_CORE_QUEUE"
            WebSetting.LoginPermits, "CLAIMCORE_WEB_LOGIN_PERMITS"
            WebSetting.SessionIdle, "CLAIMCORE_WEB_SESSION_IDLE_MINUTES"
            WebSetting.SessionAbsolute, "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES"
            WebSetting.OidcCaCertFile, "CLAIMCORE_OIDC_CA_CERT_FILE"
        ]
        @ digestEntries

    let all = entries |> List.map fst

    let required =
        [
            WebSetting.ConnectionFile
            WebSetting.WitnessConnectionFile
            WebSetting.DataKeyFile
            WebSetting.CertificatePath
            WebSetting.KeyRingDirectory
            WebSetting.OidcIssuer
            WebSetting.OidcClientId
            WebSetting.OidcClientSecretFile
            WebSetting.OidcApiAudience
            WebSetting.OidcCliClientId
            WebSetting.OidcServiceClientIds
        ]

    let optional =
        all |> List.filter (fun setting -> not (List.contains setting required))

    let internal digest = digestEntries |> List.map fst

    let token setting =
        entries |> List.find (fst >> (=) setting) |> snd

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type WebStartupProblem =
    | MissingSetting of WebSetting
    | InvalidSetting of WebSetting
    | OriginInvalid
    | ConnectionFileRefused
    | ConnectionFileEmpty
    | DataKeyFileInvalid
    | PoolCapacityInsufficient
    | SessionLifetimeInvalid
    | OidcConfigurationInvalid
    | CertificateAccessRefused
    | CertificateInvalid
    | CertificateKeyMissing
    | AssetsMissing
    | UnsupportedInvocation
    | UnexpectedFailure
    | RuntimeOpen of RuntimeOpenFault
    | RuntimeDrainBoundExpired
    | RuntimeTeardownFailed
    | OutputDeliveryFailed

[<NoEquality; NoComparison>]
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
            | WebStartupProblem.DataKeyFileInvalid ->
                "DATA_KEY_FILE_INVALID", Some(WebSettings.token WebSetting.DataKeyFile)
            | WebStartupProblem.PoolCapacityInsufficient ->
                "POOL_CAPACITY_INSUFFICIENT", Some(WebSettings.token WebSetting.ConnectionFile)
            | WebStartupProblem.RuntimeOpen fault ->
                (match fault with
                 | RuntimeOpenFault.InvalidTransport -> "RUNTIME_INVALID_TRANSPORT"
                 | RuntimeOpenFault.UnsupportedServer -> "RUNTIME_UNSUPPORTED_SERVER"
                 | RuntimeOpenFault.UnsupportedDatabase -> "RUNTIME_UNSUPPORTED_DATABASE"
                 | RuntimeOpenFault.UnsafeRole -> "RUNTIME_UNSAFE_ROLE"
                 | RuntimeOpenFault.SchemaMismatch -> "RUNTIME_SCHEMA_MISMATCH"
                 | RuntimeOpenFault.WitnessMismatch -> "RUNTIME_WITNESS_MISMATCH"
                 | RuntimeOpenFault.InstallationInvalid -> "RUNTIME_INSTALLATION_INVALID"
                 | RuntimeOpenFault.DataKeyInvalid -> "RUNTIME_DATA_KEY_INVALID"
                 | RuntimeOpenFault.HeadMismatch -> "RUNTIME_HEAD_MISMATCH"
                 | RuntimeOpenFault.Unavailable -> "RUNTIME_UNAVAILABLE"
                 | RuntimeOpenFault.Cancelled -> "RUNTIME_CANCELLED"),
                None
            | WebStartupProblem.RuntimeDrainBoundExpired -> "RUNTIME_DRAIN_BOUND_EXPIRED", None
            | WebStartupProblem.RuntimeTeardownFailed -> "RUNTIME_TEARDOWN_FAILED", None
            | WebStartupProblem.OutputDeliveryFailed -> "OUTPUT_DELIVERY_FAILED", None
            | value -> (string value).ToUpperInvariant(), None

        JsonSerializer.SerializeToUtf8Bytes
            {|
                kind = "startupFailure"
                code = code
                setting = setting
            |}
