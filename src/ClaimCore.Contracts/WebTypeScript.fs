namespace ClaimCore.Contracts

open System.Text
open ClaimCore.Application

[<RequireQualifiedAccess>]
module WebTypeScript =
    let private reference = Schema.reference

    let private alias name schema =
        let rendered = Schema.typeScript schema

        let value =
            if name = "CaseFields" then
                rendered + " & Readonly<Record<string, string | null>>"
            else
                rendered

        let export = if name = "ChangeSummary" then "type " else "export type "
        export + name + " = " + value + ";"

    let private moduleBytes imports aliases trailing =
        let lines =
            [ "/* Generated from ClaimCore.Contracts. Do not edit. */" ]
            @ imports
            @ (if imports.IsEmpty then [] else [ "" ])
            @ (aliases |> List.map (fun (name, schema) -> alias name schema))
            @ trailing
            @ [ "" ]

        String.concat "\n" lines |> Encoding.UTF8.GetBytes

    let private scalarDescriptor =
        Schema.oneOf
            [
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "TEXT")
                        WireSchema.property "minimumCharacters" (Schema.integer (Some 0L) None)
                        WireSchema.property "maximumCharacters" (Schema.integer (Some 0L) None)
                        WireSchema.property "requiresNonBlank" Schema.boolean
                        WireSchema.property "rejectsSurroundingWhitespace" Schema.boolean
                        WireSchema.property "rejectsControlCharacters" Schema.boolean
                        WireSchema.property "requiresWellFormedUnicode" Schema.boolean
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "CALENDAR_DATE")
                        WireSchema.property "exactFormat" WireSchema.text
                        WireSchema.property "minimum" WireSchema.date
                        WireSchema.property "maximum" WireSchema.date
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "AMOUNT")
                        WireSchema.property "grammar" WireSchema.text
                        WireSchema.property "maximumIntegerDigits" (Schema.integer (Some 0L) None)
                        WireSchema.property
                            "maximumFractionalDigits"
                            (Schema.integer (Some 0L) None)
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "CURRENCY")
                        WireSchema.property "grammar" WireSchema.text
                        WireSchema.property "exactCharacters" (Schema.integer (Some 0L) None)
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "CASE_STATUS")
                        WireSchema.property
                            "allowedValues"
                            (WireSchema.array CoreValueSchemas.status)
                    ]
            ]

    let private fieldDescriptor =
        WireSchema.objectOf
            [
                WireSchema.property "name" WireSchema.text
                WireSchema.property "nativeName" WireSchema.text
                WireSchema.property "label" WireSchema.text
                WireSchema.property "meaning" WireSchema.text
                WireSchema.property "allowsAbsence" Schema.boolean
                WireSchema.property "scalar" scalarDescriptor
            ]

    let private commandInputDescriptor =
        Schema.oneOf
            [
                WireSchema.objectOf
                    [
                        WireSchema.property "fieldName" WireSchema.text
                        WireSchema.property "prefill" (WireSchema.token "BLANK")
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "fieldName" WireSchema.text
                        WireSchema.property "prefill" (WireSchema.token "CURRENT_FIELD")
                        WireSchema.property "currentField" WireSchema.text
                    ]
            ]

    let private correctionGroupDescriptor =
        WireSchema.objectOf
            [
                WireSchema.property "name" WireSchema.text
                WireSchema.property "label" WireSchema.text
                WireSchema.property "meaning" WireSchema.text
                WireSchema.property
                    "actions"
                    (WireSchema.array (WireSchema.enumeration [ "KEEP"; "REPLACE"; "CLEAR" ]))
                WireSchema.property
                    "replaceFields"
                    (WireSchema.array (reference "CommandInputDescriptor"))
            ]

    let private commandInputShape =
        Schema.oneOf
            [
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "FIELDS")
                        WireSchema.property
                            "fields"
                            (WireSchema.array (reference "CommandInputDescriptor"))
                    ]
                WireSchema.objectOf
                    [
                        WireSchema.property "kind" (WireSchema.token "CORRECTION_GROUPS")
                        WireSchema.property
                            "groups"
                            (WireSchema.array (reference "CorrectionGroupDescriptor"))
                    ]
            ]

    let private commandDescriptor =
        WireSchema.objectOf
            [
                WireSchema.property "kind" CoreValueSchemas.command
                WireSchema.property "label" WireSchema.text
                WireSchema.property "meaning" WireSchema.text
                WireSchema.property "inputs" (reference "CommandInputShape")
            ]

    let private semanticDefinition (semantic: SemanticCoreContract) =
        WireSchema.objectOf
            [
                WireSchema.property "contractKind" (WireSchema.token "SEMANTIC_CORE_V1")
                WireSchema.property "application" (WireSchema.token semantic.Application)
                WireSchema.property "scope" (WireSchema.token semantic.Scope)
                WireSchema.property
                    "canonicalCommandFormat"
                    (WireSchema.number semantic.CanonicalCommandFormat)
                WireSchema.property
                    "requestFingerprintVersion"
                    (WireSchema.number semantic.RequestFingerprintVersion)
                WireSchema.property
                    "recoveryEnvelopeFormat"
                    (WireSchema.number semantic.RecoveryEnvelopeFormat)
                WireSchema.property "defaultPageSize" (WireSchema.number semantic.DefaultPageSize)
                WireSchema.property "maximumPageSize" (WireSchema.number semantic.MaximumPageSize)
                WireSchema.property "requestByteLimit" (WireSchema.number semantic.RequestByteLimit)
                WireSchema.property "fields" (WireSchema.array (reference "FieldDescriptor"))
                WireSchema.property "commands" (WireSchema.array (reference "CommandDescriptor"))
                WireSchema.property "statuses" (WireSchema.array CoreValueSchemas.status)
                WireSchema.property
                    "rules"
                    (WireSchema.array (
                        WireSchema.objectOf
                            [
                                WireSchema.property "identifier" WireSchema.text
                                WireSchema.property
                                    "category"
                                    (WireSchema.enumeration [ "CROSS_FIELD"; "TRANSITION" ])
                                WireSchema.property "meaning" WireSchema.text
                            ]
                    ))
            ]

    let private definitions projection =
        WebSchemaDefinitions.all projection.Semantic projection.DefinitionSchema.Root
        |> Map.ofList

    let private fromDefinitions names values =
        names |> List.map (fun name -> name, Map.find name values)

    let private semanticModule projection =
        moduleBytes
            []
            [
                "FieldDescriptor", fieldDescriptor
                "CommandInputDescriptor", commandInputDescriptor
                "CorrectionGroupDescriptor", correctionGroupDescriptor
                "CommandInputShape", commandInputShape
                "CommandDescriptor", commandDescriptor
                "SemanticDefinition", semanticDefinition projection.Semantic
            ]
            []

    let private coreModule projection values =
        let aliases =
            fromDefinitions
                [
                    "HostFailure"
                    "Rejection"
                    "Fault"
                    "SessionSnapshot"
                    "DefinitionPayload"
                    "CaseFields"
                    "CaseView"
                    "CurrentCase"
                    "CaseSummary"
                    "Receipt"
                    "ChangeSummary"
                    "HistoryEntry"
                    "RuntimeContext"
                    "FieldDiff"
                ]
                values
            @ [ "CommandDraft", ProjectionSchema.commandDraft projection.Semantic ]

        moduleBytes
            [ "import type { SemanticDefinition } from \"./web-v2.types.semantic\";" ]
            aliases
            []

    let private recoveryModule values =
        let aliases =
            fromDefinitions
                [
                    "RecoveryRejection"
                    "PreparationSummary"
                    "RevokedOperation"
                    "PreparationDetails"
                    "RecoveryListItem"
                    "RecoveryPage"
                    "AdvisoryReview"
                    "RecoveryImportPreview"
                    "RecoveryDetails"
                    "RecoveryInspection"
                    "DefiniteExecution"
                ]
                values

        moduleBytes
            [
                "import type { CaseView, FieldDiff, Fault, Receipt, Rejection, RuntimeContext } from \"./web-v2.types.core\";"
            ]
            aliases
            []

    let private barrel =
        [
            "/* Generated from ClaimCore.Contracts. Do not edit. */"
            "export type * from \"./web-v2.types.semantic\";"
            "export type * from \"./web-v2.types.core\";"
            "export type * from \"./web-v2.types.recovery\";"
            "export type * from \"./web-v2.types.responses\";"
            ""
        ]
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let artifacts (projection: ContractModel) =
        let values = definitions projection

        [
            "web-v2.types.semantic.ts", semanticModule projection
            "web-v2.types.core.ts", coreModule projection values
            "web-v2.types.recovery.ts", recoveryModule values
            "web-v2.types.ts", barrel
        ]
        @ WebTypeScriptResponses.artifacts projection
