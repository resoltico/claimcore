namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
type ProtocolProblem =
    | DocumentTooLarge
    | FrameTooLarge
    | BlankFrame
    | CrLfForbidden
    | ByteOrderMark
    | InvalidUtf8
    | InvalidUnicode
    | InvalidJson
    | ExpectedObject
    | ExpectedString
    | ExpectedBoolean
    | ExpectedNumber
    | DuplicateProperty
    | UnknownProperty
    | MissingProperty
    | IntegerRange
    | InvalidToken
    | InvalidUuid
    | InvalidDigest
    | InvalidRevision
    | UnknownCommand
    | CorrectionAction
    | UnknownEndpoint
    | TimeoutForbidden
    | RecoveryView
    | DismissConfirmation
    | RetainConfirmation
    | SourceAccess
    | SourceLimit
    | SourceEncoding
    | SourcePlatform
    | ConnectionMissing
    | ConnectionAccess
    | ConnectionEmpty
    | UnsupportedInvocation

/// Closed protocol vocabulary. No submitted text or provider detail is retained.
module ProtocolProblems =
    let private group0 =
        [
            ProtocolProblem.DocumentTooLarge,
            ("CLI_DOCUMENT_TOO_LARGE",
             "INPUT_TOO_LARGE",
             "The JSON input exceeds the configured byte limit.")
            ProtocolProblem.FrameTooLarge,
            ("CLI_FRAME_TOO_LARGE",
             "FRAME_TOO_LARGE",
             "The NDJSON frame exceeds the configured byte limit.")
            ProtocolProblem.BlankFrame,
            ("CLI_BLANK_FRAME", "BLANK_FRAME", "NDJSON frames must contain one JSON object.")
            ProtocolProblem.CrLfForbidden,
            ("CLI_CR_LF_FORBIDDEN", "CRLF_FORBIDDEN", "NDJSON uses LF as its only frame terminator.")
            ProtocolProblem.ByteOrderMark,
            ("CLI_BYTE_ORDER_MARK",
             "UTF8_BOM_FORBIDDEN",
             "UTF-8 input must not start with a byte-order mark.")
            ProtocolProblem.InvalidUtf8,
            ("CLI_INVALID_UTF8", "INVALID_UTF8", "JSON input must be valid UTF-8.")
            ProtocolProblem.InvalidUnicode,
            ("CLI_INVALID_UNICODE",
             "INVALID_UNICODE",
             "JSON text must contain valid Unicode scalars.")
            ProtocolProblem.InvalidJson,
            ("CLI_INVALID_JSON", "INVALID_JSON", "Input must be one strict JSON document.")
        ]

    let private group1 =
        [
            ProtocolProblem.ExpectedObject,
            ("CLI_EXPECTED_OBJECT", "INVALID_SHAPE", "Expected a JSON object.")
            ProtocolProblem.ExpectedString,
            ("CLI_EXPECTED_STRING", "INVALID_SHAPE", "Expected a JSON string.")
            ProtocolProblem.ExpectedBoolean,
            ("CLI_EXPECTED_BOOLEAN", "INVALID_SHAPE", "Expected a JSON boolean.")
            ProtocolProblem.ExpectedNumber,
            ("CLI_EXPECTED_NUMBER", "INVALID_SHAPE", "Expected a JSON number.")
            ProtocolProblem.DuplicateProperty,
            ("CLI_DUPLICATE_PROPERTY", "DUPLICATE_KEY", "JSON object keys must be unique.")
            ProtocolProblem.UnknownProperty,
            ("CLI_UNKNOWN_PROPERTY",
             "UNKNOWN_PROPERTY",
             "The JSON object contains an unknown property.")
            ProtocolProblem.MissingProperty,
            ("CLI_MISSING_PROPERTY",
             "MISSING_PROPERTY",
             "The JSON object is missing a required property.")
            ProtocolProblem.IntegerRange,
            ("CLI_INTEGER_RANGE", "INVALID_RANGE", "The number is outside its permitted range.")
        ]

    let private group2 =
        [
            ProtocolProblem.InvalidToken,
            ("CLI_INVALID_TOKEN", "INVALID_VALUE", "The value is not one of the permitted tokens.")
            ProtocolProblem.InvalidUuid,
            ("CLI_INVALID_UUID", "INVALID_UUID", "Use a non-empty canonical lowercase UUID.")
            ProtocolProblem.InvalidDigest,
            ("CLI_INVALID_DIGEST", "INVALID_DIGEST", "Use a lowercase SHA-256 digest.")
            ProtocolProblem.InvalidRevision,
            ("CLI_INVALID_REVISION",
             "INVALID_REVISION",
             "Use a canonical unsigned revision below Int64.MaxValue.")
            ProtocolProblem.UnknownCommand,
            ("CLI_UNKNOWN_COMMAND",
             "INVALID_COMMAND",
             "The command kind is not declared by the semantic contract.")
            ProtocolProblem.CorrectionAction,
            ("CLI_CORRECTION_ACTION",
             "INVALID_COMMAND",
             "The correction action is not declared by the semantic contract.")
            ProtocolProblem.UnknownEndpoint,
            ("CLI_UNKNOWN_ENDPOINT",
             "UNKNOWN_ENDPOINT",
             "The endpoint is not declared by the generated CLI contract.")
            ProtocolProblem.TimeoutForbidden,
            ("CLI_TIMEOUT_FORBIDDEN",
             "TIMEOUT_FORBIDDEN",
             "This endpoint does not permit caller cancellation.")
        ]

    let private group3 =
        [
            ProtocolProblem.RecoveryView,
            ("CLI_RECOVERY_VIEW", "INVALID_RECOVERY_VIEW", "Use PENDING or TERMINAL recovery view.")
            ProtocolProblem.DismissConfirmation,
            ("CLI_DISMISS_CONFIRMATION",
             "AFFIRMATION_REQUIRED",
             "Recovery dismissal requires confirmed: true.")
            ProtocolProblem.RetainConfirmation,
            ("CLI_RETAIN_CONFIRMATION",
             "AFFIRMATION_REQUIRED",
             "Recovery retention requires confirmed: true.")
            ProtocolProblem.SourceAccess,
            ("CLI_SOURCE_ACCESS",
             "PRIVATE_FILE_ERROR",
             "The private source could not be read securely.")
            ProtocolProblem.SourceLimit,
            ("CLI_SOURCE_LIMIT",
             "PRIVATE_FILE_ERROR",
             "The private source exceeds the supported byte limit.")
            ProtocolProblem.SourceEncoding,
            ("CLI_SOURCE_ENCODING",
             "PRIVATE_FILE_ERROR",
             "The private source must contain valid UTF-8.")
            ProtocolProblem.SourcePlatform,
            ("CLI_SOURCE_PLATFORM",
             "PRIVATE_FILE_ERROR",
             "Private source access is unsupported on this platform.")
            ProtocolProblem.ConnectionMissing,
            ("CLI_CONNECTION_MISSING",
             "CONFIGURATION_ERROR",
             "Set CLAIMCORE_CONNECTION_FILE to a private application connection file.")
        ]

    let private group4 =
        [
            ProtocolProblem.ConnectionAccess,
            ("CLI_CONNECTION_ACCESS",
             "CONFIGURATION_ERROR",
             "The connection file must be an owner-only regular UTF-8 file at a safe absolute path.")
            ProtocolProblem.ConnectionEmpty,
            ("CLI_CONNECTION_EMPTY", "CONFIGURATION_ERROR", "The connection file is empty.")
            ProtocolProblem.UnsupportedInvocation,
            ("CLI_UNSUPPORTED_INVOCATION",
             "UNSUPPORTED_INVOCATION",
             "Unsupported invocation. Run 'claimcore help' for the CLI v3 grammar.")
        ]

    let private entries = group0 @ group1 @ group2 @ group3 @ group4
    let all = entries |> List.map fst

    let private policy reason =
        entries |> List.find (fst >> (=) reason) |> snd

    let token reason = let id, _, _ = policy reason in id
    let code reason = let _, code, _ = policy reason in code

    let render reason =
        let _, _, message = policy reason in message
