namespace ClaimCore.Contract

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
    | [<MessageKey("ui.characterWarning")>] CharacterWarning of characters: string
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
    | [<MessageKey("ui.action")>] ActionLabel
    | [<MessageKey("ui.availableActs")>] AvailableActsLabel
    | [<MessageKey("ui.enabled")>] EnabledLabel
    | [<MessageKey("ui.reviewTitle")>] ReviewTitle
    | [<MessageKey("ui.revisionLabel")>] RevisionLabel

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

    let contracts =
        let definitions =
            FSharpType.GetUnionCases typeof<CliMessage> |> Array.map definition

        let result =
            definitions
            |> Array.map (fun (key, fields) -> key, Set.ofArray fields)
            |> Map.ofArray

        if result.Count <> definitions.Length then
            invalidOp "CLI message identities must be unique."

        result

    let produce message =
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
