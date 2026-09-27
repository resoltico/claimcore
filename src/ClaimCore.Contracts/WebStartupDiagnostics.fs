namespace ClaimCore.Contracts

open System.Buffers
open System.Text.Json
open ClaimCore.Application

[<RequireQualifiedAccess>]
type WebSetting =
    | ConnectionFile
    | WitnessConnectionFile
    | WitnessKeyFile
    | SuppressionKeyFile
    | RecoveryArtifactKeyFile
    | CertificatePath
    | StateDirectory
    | Origin
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
            WebSetting.WitnessConnectionFile, "CLAIMCORE_WITNESS_CONNECTION_FILE"
            WebSetting.WitnessKeyFile, "CLAIMCORE_WITNESS_KEY_FILE"
            WebSetting.SuppressionKeyFile, "CLAIMCORE_SUPPRESSION_KEY_FILE"
            WebSetting.RecoveryArtifactKeyFile, "CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE"
            WebSetting.CertificatePath, "CLAIMCORE_WEB_CERTIFICATE_PATH"
            WebSetting.StateDirectory, "CLAIMCORE_WEB_STATE_DIR"
            WebSetting.Origin, "CLAIMCORE_WEB_ORIGIN"
            WebSetting.JsonLimit, "CLAIMCORE_WEB_MAX_JSON_BYTES"
            WebSetting.CorePermits, "CLAIMCORE_WEB_CORE_PERMITS"
            WebSetting.CoreQueue, "CLAIMCORE_WEB_CORE_QUEUE"
            WebSetting.LoginPermits, "CLAIMCORE_WEB_LOGIN_PERMITS"
            WebSetting.SessionIdle, "CLAIMCORE_WEB_SESSION_IDLE_MINUTES"
            WebSetting.SessionAbsolute, "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES"
            WebSetting.OidcCaCertFile, "CLAIMCORE_OIDC_CA_CERT_FILE"
        ]

    let all = entries |> List.map fst

    let token setting =
        entries |> List.find (fst >> (=) setting) |> snd

[<RequireQualifiedAccess>]
type WebStartupProblem =
    | MissingSetting of WebSetting
    | InvalidSetting of WebSetting
    | RuntimeOpen of RuntimeOpenFault
    | OriginInvalid
    | SessionLifetimeInvalid
    | OidcConfigurationInvalid
    | StateDirectoryRefused
    | StateLockRefused
    | CertificateAccessRefused
    | CertificateInvalid
    | CertificateKeyMissing
    | ConnectionFileRefused
    | ConnectionFileEmpty
    | WitnessConnectionFileRefused
    | WitnessConnectionFileEmpty
    | WitnessKeyFileRefused
    | SuppressionKeyFileRefused
    | RecoveryArtifactKeyFileRefused
    | AssetsMissing
    | UnsupportedInvocation
    | UnexpectedFailure

exception WebStartupException of WebStartupProblem

module WebStartupDiagnostics =
    let refuse reason = raise (WebStartupException reason)

    let private group0 =
        [
            WebStartupProblem.OriginInvalid,
            ("WEB_ORIGIN_CONFIGURATION_INVALID",
             "Configure one HTTPS localhost origin without a path.")
            WebStartupProblem.SessionLifetimeInvalid,
            ("WEB_SESSION_LIFETIME_INVALID", "Session lifetimes must be positive and ordered.")
            WebStartupProblem.OidcConfigurationInvalid,
            ("WEB_OIDC_CONFIGURATION_INVALID",
             "Configure one valid HTTPS OIDC issuer and private client credentials.")
            WebStartupProblem.StateDirectoryRefused,
            ("WEB_STATE_DIRECTORY_REFUSED",
             "The state directory must be an owner-private absolute directory without links.")
            WebStartupProblem.StateLockRefused,
            ("WEB_STATE_LOCK_REFUSED", "The owner-private Web state lock could not be acquired.")
        ]

    let private group1 =
        [
            WebStartupProblem.CertificateAccessRefused,
            ("WEB_CERTIFICATE_ACCESS_REFUSED",
             "The certificate must be a bounded private PKCS#12 file.")
            WebStartupProblem.CertificateInvalid,
            ("WEB_CERTIFICATE_INVALID", "The certificate is not valid for localhost HTTPS.")
            WebStartupProblem.CertificateKeyMissing,
            ("WEB_CERTIFICATE_KEY_MISSING", "The certificate must include a private key.")
            WebStartupProblem.ConnectionFileRefused,
            ("WEB_CONNECTION_FILE_REFUSED",
             "The application connection must be in a bounded private UTF-8 file.")
            WebStartupProblem.WitnessConnectionFileRefused,
            ("WEB_WITNESS_CONNECTION_FILE_REFUSED",
             "The witness connection must be in a bounded private UTF-8 file.")
            WebStartupProblem.WitnessKeyFileRefused,
            ("WEB_WITNESS_KEY_FILE_REFUSED",
             "Configure an absolute owner-private witness key-ring file; the runtime validates its bounded contents.")
            WebStartupProblem.SuppressionKeyFileRefused,
            ("WEB_SUPPRESSION_KEY_FILE_REFUSED",
             "Configure an absolute owner-private suppression key file; the runtime verifies it against the installation marker.")
            WebStartupProblem.RecoveryArtifactKeyFileRefused,
            ("WEB_RECOVERY_ARTIFACT_KEY_FILE_REFUSED",
             "Configure an absolute owner-private recovery artifact key-ring file.")
        ]

    let private group2 =
        [
            WebStartupProblem.ConnectionFileEmpty,
            ("WEB_CONNECTION_FILE_EMPTY", "The application connection file is empty.")
            WebStartupProblem.WitnessConnectionFileEmpty,
            ("WEB_WITNESS_CONNECTION_FILE_EMPTY", "The witness connection file is empty.")
            WebStartupProblem.AssetsMissing,
            ("WEB_ASSETS_MISSING", "Publish the verified Web assets before starting the host.")
            WebStartupProblem.UnsupportedInvocation,
            ("WEB_INVOCATION_UNSUPPORTED", "Run ClaimCore.Web help for supported arguments.")
            WebStartupProblem.UnexpectedFailure,
            ("WEB_PROCESS_FAILED",
             "The local Web host failed. Preserve operation identities and do not infer command outcomes from this process failure.")
        ]

    let private entries = group0 @ group1 @ group2

    let private runtime =
        function
        | RuntimeOpenFault.RuntimeConfigurationInvalid -> "WEB_RUNTIME_CONFIGURATION_INVALID"
        | RuntimeOpenFault.RuntimeSchemaMismatch -> "WEB_RUNTIME_SCHEMA_MISMATCH"
        | RuntimeOpenFault.RuntimeStoreUnavailable -> "WEB_RUNTIME_UNAVAILABLE"
        | RuntimeOpenFault.RuntimeStoreIntegrityError -> "WEB_RUNTIME_INTEGRITY_ERROR"
        | RuntimeOpenFault.RuntimeCancelled -> "WEB_RUNTIME_OPEN_CANCELLED"

    let private policy =
        function
        | WebStartupProblem.MissingSetting _ ->
            "WEB_SETTING_MISSING", "A required private configuration setting is missing."
        | WebStartupProblem.InvalidSetting _ ->
            "WEB_SETTING_INVALID", "A configuration setting is outside its supported range."
        | WebStartupProblem.RuntimeOpen reason ->
            runtime reason, "The configured application runtime could not be opened."
        | reason -> entries |> List.find (fst >> (=) reason) |> snd

    let all =
        (entries |> List.map fst)
        @ (WebSettings.all
           |> List.collect (fun setting ->
               [
                   WebStartupProblem.MissingSetting setting
                   WebStartupProblem.InvalidSetting setting
               ]))
        @ ([
            RuntimeOpenFault.RuntimeConfigurationInvalid
            RuntimeOpenFault.RuntimeSchemaMismatch
            RuntimeOpenFault.RuntimeStoreUnavailable
            RuntimeOpenFault.RuntimeStoreIntegrityError
            RuntimeOpenFault.RuntimeCancelled
           ]
           |> List.map WebStartupProblem.RuntimeOpen)

    let token reason = policy reason |> fst

    let encode reason =
        let id, message = policy reason
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("kind", "webProcessFailure")
        writer.WritePropertyName("diagnostic")
        writer.WriteStartObject()
        writer.WriteString("id", id)
        writer.WritePropertyName("parameters")
        writer.WriteStartObject()

        match reason with
        | WebStartupProblem.MissingSetting setting
        | WebStartupProblem.InvalidSetting setting ->
            writer.WriteString("setting", WebSettings.token setting)
        | _ -> ()

        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WriteString("message", message)
        writer.WriteString("recommendedAction", "STOP_AND_INVESTIGATE")
        writer.WriteEndObject()
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
