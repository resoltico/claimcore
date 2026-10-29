namespace ClaimCore.Cli

open System
open Microsoft.FSharp.Reflection

/// A producer-owned message identity. Named union fields are its exact interpolation arguments.
[<AttributeUsage(AttributeTargets.All, AllowMultiple = false)>]
type MessageKeyAttribute(key: string) =
    inherit Attribute()
    member _.Key = key

type ProducedMessage =
    {
        Key: string
        Arguments: Map<string, string>
    }

[<RequireQualifiedAccess>]
type CliMessage =
    | [<MessageKey("cli.usage.unknownCommand")>] UnknownCommand of command: string
    | [<MessageKey("cli.usage.unknownOption")>] UnknownOption of flag: string
    | [<MessageKey("cli.usage.suggestion")>] Suggestion of suggestion: string
    | [<MessageKey("cli.usage.missingOption")>] MissingOption of flag: string
    | [<MessageKey("cli.usage.missingOperand")>] MissingOperand of operand: string
    | [<MessageKey("cli.usage.extraOperand")>] ExtraOperand of operand: string
    | [<MessageKey("cli.usage.invalidInteger")>] InvalidInteger of flag: string * value: string
    | [<MessageKey("cli.usage.invalidValue")>] InvalidValue of flag: string * value: string
    | [<MessageKey("cli.usage.invalidChoice")>] InvalidChoice of
        flag: string *
        value: string *
        allowed: string
    | [<MessageKey("cli.usage.notApplicable")>] NotApplicable of flag: string * because: string
    | [<MessageKey("cli.usage.duplicateOption")>] DuplicateOption of flag: string
    | [<MessageKey("cli.usage.missingValue")>] MissingValue of flag: string
    | [<MessageKey("cli.usage.synopsis")>] Synopsis of synopsis: string
    | [<MessageKey("cli.usage.helpTopic")>] UnknownHelpTopic
    | [<MessageKey("cli.usage.helpOptions")>] InvalidHelpOptions
    | [<MessageKey("cli.usage.describeSelector")>] InvalidDescribeSelector
    | [<MessageKey("cli.usage.describeOptions")>] InvalidDescribeOptions
    | [<MessageKey("cli.usage.schemaSelector")>] InvalidSchemaSelector
    | [<MessageKey("cli.usage.schemaOptions")>] InvalidSchemaOptions
    | [<MessageKey("cli.usage.versionOptions")>] InvalidVersionOptions
    | [<MessageKey("cli.usage.invocation")>] InvalidInvocation of command: string
    | [<MessageKey("cli.usage.requestTooLarge")>] RequestTooLarge of maximumBytes: string
    | [<MessageKey("cli.delivery.unresolved")>] UnresolvedDelivery of
        retryCommand: string *
        resolveCommand: string
    | [<MessageKey("cli.configurationIssue")>] ConfigurationIssue of setting: string * issue: string
    | [<MessageKey("label.characterWarning")>] CharacterWarning of characters: string
    | [<MessageKey("cli.help.layout")>] HelpLayout of body: string
    | [<MessageKey("cli.help.call")>] HelpCall of
        maximumInvocationBytes: string *
        maximumRequestBytes: string *
        maximumResponseBytes: string *
        retryCommand: string *
        resolveCommand: string
    | [<MessageKey("cli.help.session")>] HelpSession of
        maximumInvocationBytes: string *
        maximumRequestBytes: string *
        maximumResponseBytes: string
    | [<MessageKey("cli.help.CLI_PROTOCOL_GUIDANCE")>] HelpProtocol of
        maximumInvocationBytes: string *
        maximumRequestBytes: string *
        maximumResponseBytes: string
    | [<MessageKey("cli.help.configuration")>] HelpConfiguration of
        maximumSecretBytes: string *
        maximumTrustRootBytes: string
    | [<MessageKey("cli.help.CLI_CONFIGURATION_ISSUE")>] HelpConfigurationIssue of
        maximumSecretBytes: string *
        maximumTrustRootBytes: string
    | [<MessageKey("cli.help.delivery")>] HelpDelivery of
        retryCommand: string *
        resolveCommand: string
    | [<MessageKey("label.action")>] ActionLabel
    | [<MessageKey("label.availableActs")>] AvailableActsLabel
    | [<MessageKey("label.enabled")>] EnabledLabel
    | [<MessageKey("label.reviewTitle")>] ReviewTitle
    | [<MessageKey("label.revisionLabel")>] RevisionLabel

    | [<MessageKey("cli.text.state")>] StateLabel
    | [<MessageKey("cli.text.next")>] NextLabel
    | [<MessageKey("cli.text.authorityHistoryEmpty")>] AuthorityHistoryEmpty
    | [<MessageKey("cli.text.grants")>] GrantsLabel
    | [<MessageKey("cli.text.code")>] CodeLabel
    | [<MessageKey("cli.text.uncertain")>] UncertainDelivery
    | [<MessageKey("cli.family.case")>] CaseFamily
    | [<MessageKey("cli.family.authority")>] AuthorityFamily
    | [<MessageKey("cli.family.operation")>] OperationFamily
    | [<MessageKey("cli.family.context")>] ContextFamily
    | [<MessageKey("cli.family.call")>] CallFamily
    | [<MessageKey("cli.family.session")>] SessionFamily
    | [<MessageKey("cli.family.describe")>] DescribeFamily
    | [<MessageKey("cli.family.schema")>] SchemaFamily
    | [<MessageKey("cli.family.help")>] HelpFamily
    | [<MessageKey("cli.help.describe")>] HelpDescribe
    | [<MessageKey("cli.help.examples")>] HelpExamples
    | [<MessageKey("cli.help.help")>] HelpHelp
    | [<MessageKey("cli.help.schema")>] HelpSchema
    | [<MessageKey("cli.help.version")>] HelpVersion
    | [<MessageKey("cli.callbackAccepted")>] CallbackAccepted
    | [<MessageKey("cli.callbackRefused")>] CallbackRefused
    | [<MessageKey("cli.beforeDispatchFailure")>] BeforeDispatchFailure
    | [<MessageKey("cli.text.cursorReached")>] CursorReached of cursor: string
    | [<MessageKey("cli.review.confirm")>] ReviewConfirmation
    | Failure of ClientFailure

/// Rendering and publication inspect the same closed producers; no separate argument-name list exists.
module CliMessages =
    let private definition (case: UnionCaseInfo) =
        let keys = case.GetCustomAttributes(typeof<MessageKeyAttribute>)

        if keys.Length <> 1 then
            invalidOp "Each CLI message requires one identity."

        let key = (keys[0] :?> MessageKeyAttribute).Key
        let fields = case.GetFields()

        if fields |> Array.exists (fun field -> field.PropertyType <> typeof<string>) then
            invalidOp "CLI message arguments must be text."

        key, fields |> Array.map _.Name

    let private failureKey =
        function
        | ClientFailure.Configuration -> "cli.failure.CONFIGURATION_INVALID"
        | ClientFailure.ConfigurationIssue _ -> "cli.configurationIssue"
        | ClientFailure.Authentication -> "cli.failure.AUTHENTICATION_FAILED"
        | ClientFailure.Unavailable -> "cli.failure.SERVICE_UNAVAILABLE"
        | ClientFailure.Cancelled -> "cli.failure.CANCELLED"
        | ClientFailure.Unexpected -> "cli.failure.INTERNAL"
        | ClientFailure.InvalidFrame -> "cli.failure.INVALID_FRAME"
        | ClientFailure.InvalidResponse -> "cli.failure.INVALID_RESPONSE"
        | ClientFailure.ContractMismatch -> "cli.failure.CONTRACT_MISMATCH"

    let private failureDefinitions =
        FSharpType.GetUnionCases typeof<ClientFailure>
        |> Array.filter (fun case -> case.GetFields().Length = 0)
        |> Array.map (fun case ->
            let failure = (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> ClientFailure
            failure, failureKey failure)
        |> Array.toList

    let contracts =
        let definitions =
            (FSharpType.GetUnionCases typeof<CliMessage>
             |> Array.filter (fun case -> case.Name <> "Failure")
             |> Array.map definition)
            |> Array.append (
                failureDefinitions |> List.map (fun (_, key) -> key, [||]) |> List.toArray
            )

        let result =
            definitions
            |> Array.map (fun (key, fields) -> key, Set.ofArray fields)
            |> Map.ofArray

        if result.Count <> definitions.Length then
            invalidOp "CLI message identities must be unique."

        result

    let rec produce message =
        match message with
        | CliMessage.Failure(ClientFailure.ConfigurationIssue(setting, issue)) ->
            produce (
                CliMessage.ConfigurationIssue(
                    InputDiagnostics.settingName setting,
                    ConfigurationIssue.token issue
                )
            )
        | CliMessage.Failure failure ->
            {
                Key = failureKey failure
                Arguments = Map.empty
            }
        | _ ->
            let case, values = FSharpValue.GetUnionFields(message, typeof<CliMessage>)
            let key, fields = definition case

            {
                Key = key
                Arguments =
                    Array.zip fields values
                    |> Array.map (fun (name, value) -> name, unbox<string> value)
                    |> Map.ofArray
            }

    let key message = (produce message).Key
