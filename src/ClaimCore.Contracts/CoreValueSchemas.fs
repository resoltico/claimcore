namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

[<RequireQualifiedAccess>]
module internal WireSchema =
    let text = Schema.string None None (Some 1) None
    let nullableText = Schema.nullable text
    let uuid = ScalarSchemas.uuid
    let revision = ScalarSchemas.decimalText
    let digest = EndpointInputs.digest

    let timestamp =
        Schema.string
            (Some "date-time")
            (Some
                "^(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}\\+00:00$")
            (Some 33)
            (Some 33)

    let date = FieldDefinitions.scalar "incidentDate" |> ScalarSchemas.scalar

    let token value = Schema.constant (TextConstant value)

    let number value =
        Schema.constant (IntegerConstant(int64 value))

    let enumeration values =
        values |> List.map TextConstant |> Schema.enumeration

    let property name schema = Schema.property name schema true
    let optional name schema = Schema.property name schema false
    let objectOf properties = Schema.objectOf false properties
    let array item = Schema.array item None None

    let tagged tag properties =
        objectOf (property "tag" (token tag) :: properties)

    let kind kind properties =
        objectOf (property "kind" (token kind) :: properties)

[<RequireQualifiedAccess>]
module CoreValueSchemas =
    let command =
        CommandKinds.all |> List.map WireTokens.command |> WireSchema.enumeration

    let status =
        CaseStatuses.all |> List.map WireTokens.status |> WireSchema.enumeration

    let action = WireTokens.actions |> WireSchema.enumeration
    let rejectionCode = WireTokens.rejectionCodes |> WireSchema.enumeration
    let faultCode = WireTokens.faultCodes |> WireSchema.enumeration

    let rejection =
        WireSchema.objectOf
            [
                WireSchema.property "code" rejectionCode
                WireSchema.property "diagnostic" RejectionDiagnosticSchemas.value
                WireSchema.property "message" WireSchema.text
                WireSchema.property
                    "field"
                    (Schema.nullable (
                        WireSchema.enumeration (
                            (InputTargets.all |> List.map snd) @ [ "limit"; "cursor" ]
                        )
                    ))
                WireSchema.property "actualRevision" (Schema.nullable WireSchema.revision)
                WireSchema.property "recommendedAction" action
            ]

    let fault =
        WireSchema.objectOf
            [
                WireSchema.property "code" faultCode
                WireSchema.property "message" WireSchema.text
                WireSchema.property "recommendedAction" action
            ]

    let caseFields (semantic: SemanticCoreContract) =
        semantic.Fields
        |> List.map (fun field ->
            let value = ScalarSchemas.scalar field.Scalar
            let schema = if field.AllowsAbsence then Schema.nullable value else value
            WireSchema.property field.Name schema)
        |> WireSchema.objectOf

    let caseView semantic =
        WireSchema.objectOf
            [
                WireSchema.property "fields" (caseFields semantic)
                WireSchema.property "revision" WireSchema.revision
            ]

    let currentCase semantic =
        WireSchema.objectOf
            [
                WireSchema.property "case" (caseView semantic)
                WireSchema.property "availableCommands" (WireSchema.array command)
            ]

    let caseSummary =
        WireSchema.objectOf
            [
                WireSchema.property "caseReference" WireSchema.text
                WireSchema.property "revision" WireSchema.revision
                WireSchema.property "status" status
            ]

    let webReceipt semantic =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "snapshot" (caseView semantic)
                WireSchema.property "recordedAt" WireSchema.timestamp
                WireSchema.property "recordedBy" WireSchema.text
                WireSchema.property "replayed" Schema.boolean
                WireSchema.property "command" command
            ]

    let cliReceipt semantic includeSnapshot =
        let snapshot =
            if includeSnapshot then
                [ WireSchema.property "snapshot" (caseView semantic) ]
            else
                []

        WireSchema.objectOf (
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "caseReference" WireSchema.text
                WireSchema.property "revision" WireSchema.revision
                WireSchema.property "command" command
                WireSchema.property "recordedAt" WireSchema.timestamp
                WireSchema.property "recordedBy" WireSchema.text
                WireSchema.property "replayed" Schema.boolean
            ]
            @ snapshot
        )

    let changeSummary =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "revision" WireSchema.revision
                WireSchema.property "command" command
                WireSchema.property "recordedAt" WireSchema.timestamp
                WireSchema.property "recordedBy" WireSchema.text
            ]

    let webHistoryEntry semantic =
        Schema.oneOf
            [
                WireSchema.tagged "SUMMARY" [ WireSchema.property "change" changeSummary ]
                WireSchema.tagged "FULL" [ WireSchema.property "receipt" (webReceipt semantic) ]
            ]

    let runtimeContext =
        WireSchema.objectOf
            [
                WireSchema.property "productVersion" WireSchema.text
                WireSchema.property "effectiveBusinessDate" WireSchema.date
                WireSchema.property "timeZoneId" WireSchema.text
            ]

    let fieldDiff =
        WireSchema.objectOf
            [
                WireSchema.property "fieldName" WireSchema.text
                WireSchema.property "before" WireSchema.nullableText
                WireSchema.property "after" WireSchema.nullableText
            ]

    let review semantic differenceName =
        WireSchema.objectOf
            [
                WireSchema.property "before" (Schema.nullable (caseView semantic))
                WireSchema.property "proposed" (caseView semantic)
                WireSchema.property differenceName (WireSchema.array fieldDiff)
                WireSchema.property "context" runtimeContext
                WireSchema.property "advisory" Schema.boolean
            ]

    let definitionPayload definition =
        WireSchema.objectOf
            [
                WireSchema.property "semanticFingerprint" WireSchema.digest
                WireSchema.property "webFingerprint" WireSchema.digest
                WireSchema.property "runtime" runtimeContext
                WireSchema.property "definition" definition
            ]
