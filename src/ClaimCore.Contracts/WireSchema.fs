namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open ClaimCore.Application
open ClaimCore.Domain

module internal WireSchema =
    let text = Schema.string None None (Some 1) None
    let uuid = ScalarSchemas.uuid
    let revision = ScalarSchemas.revision
    let sequence = ScalarSchemas.positive
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

    let reference =
        FieldDefinitions.scalar (InputField.Business BusinessField.CaseReference)
        |> ScalarSchemas.scalar

    let command = CaseActKinds.all |> List.map CaseActKinds.token |> enumeration
    let disposition = enumeration [ "ACTIVE"; "VOIDED" ]

    let timestamp =
        Schema.string
            (Some "date-time")
            (Some ProtocolProfile.CanonicalTimestampPattern)
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
