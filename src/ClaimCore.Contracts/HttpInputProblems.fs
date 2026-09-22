namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
type HttpInputProblem =
    | ExpectedObject
    | DuplicateProperty
    | UnknownProperty
    | MissingProperty
    | ExpectedString
    | NullForbidden
    | ExpectedBoolean
    | ExpectedInteger
    | InvalidRevision
    | InvalidUuid
    | InvalidDigest
    | InvalidUnicode
    | InvalidUtf8
    | InvalidJson
    | BodyTooLarge
    | BodyUnreadable
    | BodyCancelled
    | UnknownCommand
    | MissingCommandValue
    | CorrectionAction
    | LogoutShape
    | PageRange
    | HistoryDetail
    | RecoveryView
    | SourceDigestHeader

module HttpInputProblems =
    let private group0 =
        [
            HttpInputProblem.ExpectedObject,
            ("WEB_INPUT_EXPECTED_OBJECT", "Expected a JSON object.")
            HttpInputProblem.DuplicateProperty,
            ("WEB_INPUT_DUPLICATE_PROPERTY", "Duplicate JSON properties are not accepted.")
            HttpInputProblem.UnknownProperty,
            ("WEB_INPUT_UNKNOWN_PROPERTY", "The JSON object contains an unknown property.")
            HttpInputProblem.MissingProperty,
            ("WEB_INPUT_MISSING_PROPERTY", "A required JSON property is missing.")
            HttpInputProblem.ExpectedString,
            ("WEB_INPUT_EXPECTED_STRING", "Expected a JSON string.")
            HttpInputProblem.NullForbidden,
            ("WEB_INPUT_NULL_FORBIDDEN", "JSON null is not accepted here.")
            HttpInputProblem.ExpectedBoolean,
            ("WEB_INPUT_EXPECTED_BOOLEAN", "Expected a JSON boolean.")
            HttpInputProblem.ExpectedInteger,
            ("WEB_INPUT_EXPECTED_INTEGER", "Expected a JSON integer.")
        ]

    let private group1 =
        [
            HttpInputProblem.InvalidRevision,
            ("WEB_INPUT_INVALID_REVISION",
             "Expected a canonical non-negative revision below Int64.MaxValue.")
            HttpInputProblem.InvalidUuid,
            ("WEB_INPUT_INVALID_UUID", "Use one non-empty canonical lowercase UUID.")
            HttpInputProblem.InvalidDigest,
            ("WEB_INPUT_INVALID_DIGEST", "Use one lowercase SHA-256 digest.")
            HttpInputProblem.InvalidUnicode,
            ("WEB_INPUT_INVALID_UNICODE", "Malformed Unicode escape in JSON text.")
            HttpInputProblem.InvalidUtf8,
            ("WEB_INPUT_INVALID_UTF8", "The request must be valid UTF-8.")
            HttpInputProblem.InvalidJson,
            ("WEB_INPUT_INVALID_JSON", "Malformed JSON or an unsupported JSON shape.")
            HttpInputProblem.BodyTooLarge,
            ("WEB_INPUT_BODY_TOO_LARGE", "The request exceeds the configured byte limit.")
            HttpInputProblem.BodyUnreadable,
            ("WEB_INPUT_BODY_UNREADABLE", "The request body could not be read.")
        ]

    let private group2 =
        [
            HttpInputProblem.BodyCancelled,
            ("WEB_INPUT_BODY_CANCELLED", "The request body read was cancelled.")
            HttpInputProblem.UnknownCommand,
            ("WEB_INPUT_UNKNOWN_COMMAND", "The command token is not supported.")
            HttpInputProblem.MissingCommandValue,
            ("WEB_INPUT_MISSING_COMMAND_VALUE", "A required command value is missing.")
            HttpInputProblem.CorrectionAction,
            ("WEB_INPUT_CORRECTION_ACTION",
             "The correction action is not declared by the semantic contract.")
            HttpInputProblem.LogoutShape,
            ("WEB_INPUT_LOGOUT_SHAPE", "Logout requires an empty JSON object.")
            HttpInputProblem.PageRange,
            ("WEB_INPUT_PAGE_RANGE", "The requested page size is outside the supported range.")
            HttpInputProblem.HistoryDetail,
            ("WEB_INPUT_HISTORY_DETAIL", "The history detail is not supported.")
            HttpInputProblem.RecoveryView,
            ("WEB_INPUT_RECOVERY_VIEW", "Use PENDING or TERMINAL recovery view.")
        ]

    let private group3 =
        [
            HttpInputProblem.SourceDigestHeader,
            ("WEB_INPUT_SOURCE_DIGEST_HEADER", "A canonical source digest header is required.")
        ]

    let private entries = group0 @ group1 @ group2 @ group3
    let all = entries |> List.map fst

    let private policy reason =
        entries |> List.find (fst >> (=) reason) |> snd

    let token reason = policy reason |> fst
    let render reason = policy reason |> snd
