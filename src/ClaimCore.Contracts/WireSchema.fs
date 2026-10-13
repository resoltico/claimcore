namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

module internal WireSchema =
    let text = Schema.string None None (Some 1) None
    let uuid = ScalarSchemas.uuid
    let revision = ScalarSchemas.decimalText
    let sequence = ScalarSchemas.positiveInt64Text
    let digest = Schema.string None (Some "^[0-9a-f]{64}$") (Some 64) (Some 64)
    let token value = Schema.constant (TextConstant value)

    let enumeration values =
        values |> List.map TextConstant |> Schema.enumeration

    let property name schema = Schema.property name schema true
    let optional name schema = Schema.property name schema false
    let objectOf properties = Schema.objectOf false properties
    let array item = Schema.array item None None

    let tagged tag properties =
        objectOf (property "tag" (token tag) :: properties)

    let reference = FieldDefinitions.scalar "caseReference" |> ScalarSchemas.scalar
    let command = CommandKinds.all |> List.map CommandKinds.token |> enumeration
    let disposition = enumeration [ "ACTIVE"; "VOIDED" ]

    let timestamp =
        Schema.string
            (Some "date-time")
            (Some
                "^(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}0\\+00:00$")
            (Some 33)
            (Some 33)

    let document name root definitions =
        {
            Identifier = "https://claimcore.local/contracts/" + name
            Title = "ClaimCore " + name
            Root = root
            Definitions = definitions
        }

module WireTokens =
    let action =
        function
        | RecommendedAction.CorrectInput -> "CORRECT_INPUT"
        | RecommendedAction.ReadCurrent -> "READ_CURRENT"
        | RecommendedAction.RetrySafe -> "RETRY_SAFE"
        | RecommendedAction.ReplayExact -> "REPLAY_EXACT"
        | RecommendedAction.Reauthenticate -> "REAUTHENTICATE"
        | RecommendedAction.StopAndInvestigate -> "STOP_AND_INVESTIGATE"
        | RecommendedAction.NoneRequired -> "NONE_REQUIRED"

    let rejectionCodes =
        [
            RejectionCode.InvalidInput, "INVALID_INPUT"
            RejectionCode.CaseNotFound, "CASE_NOT_FOUND"
            RejectionCode.CaseAlreadyExists, "CASE_ALREADY_EXISTS"
            RejectionCode.VersionConflict, "VERSION_CONFLICT"
            RejectionCode.CaseClosed, "CASE_CLOSED"
            RejectionCode.AmendmentRequiresUndecided, "AMENDMENT_REQUIRES_UNDECIDED"
            RejectionCode.DecisionRequired, "DECISION_REQUIRED"
            RejectionCode.PaymentAlreadyRecorded, "PAYMENT_ALREADY_RECORDED"
            RejectionCode.PaymentNotRecorded, "PAYMENT_NOT_RECORDED"
            RejectionCode.DecisionAlreadyPaid, "DECISION_ALREADY_PAID"
            RejectionCode.AlreadyClosed, "ALREADY_CLOSED"
            RejectionCode.AlreadyOpened, "ALREADY_OPENED"
            RejectionCode.ZeroDecisionCannotBePaid, "ZERO_DECISION_CANNOT_BE_PAID"
            RejectionCode.IdempotencyConflict, "IDEMPOTENCY_CONFLICT"
            RejectionCode.ResourceUnavailable, "RESOURCE_UNAVAILABLE"
            RejectionCode.CaseVoided, "CASE_VOIDED"
            RejectionCode.CaseErased, "CASE_ERASED"
            RejectionCode.InvalidReason, "INVALID_REASON"
            RejectionCode.AlreadyVoided, "ALREADY_VOIDED"
            RejectionCode.NotVoided, "NOT_VOIDED"
            RejectionCode.LastOwner, "LAST_OWNER"
            RejectionCode.ActorAlreadyRegistered, "ACTOR_ALREADY_REGISTERED"
            RejectionCode.ActorUnknown, "ACTOR_UNKNOWN"
            RejectionCode.HumanPrincipalRequired, "HUMAN_PRINCIPAL_REQUIRED"
        ]

    let rejectionCode code =
        rejectionCodes |> List.find (fst >> (=) code) |> snd

    let faultCode =
        function
        | FaultCode.StoreUnavailable -> "STORE_UNAVAILABLE"
        | FaultCode.StoreIntegrityError -> "STORE_INTEGRITY_ERROR"
        | FaultCode.SchemaMismatch -> "SCHEMA_MISMATCH"
        | FaultCode.OperationContentConflict -> "OPERATION_CONTENT_CONFLICT"
