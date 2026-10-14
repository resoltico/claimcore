namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain
open WireSchema

module internal ValueSchemas =
    let private ref = Schema.reference

    let context =
        objectOf
            [
                property "productVersion" text
                property
                    "effectiveBusinessDate"
                    (ScalarSchemas.scalar (FieldDefinitions.scalar "incidentDate"))
                property "timeZoneId" text
            ]

    let fields =
        SemanticContract.current.Fields
        |> List.map (fun field ->
            property
                field.Name
                (let s = ScalarSchemas.scalar field.Scalar in
                 if field.AllowsAbsence then Schema.nullable s else s))
        |> objectOf

    let view =
        objectOf [ property "fields" (ref "CaseFields"); property "revision" revision ]

    let position =
        objectOf [ property "sequence" sequence; property "entryHash" digest ]

    let act =
        enumeration (
            (CommandKinds.all |> List.map CommandKinds.token)
            @ [ "VOID"; "REINSTATE"; "ERASE" ]
        )

    let record =
        Schema.oneOf
            [
                objectOf
                    [
                        property "state" (token "LIVE")
                        property "case" (ref "CaseView")
                        property "disposition" disposition
                        property "availableCommands" (array command)
                    ]
                objectOf
                    [
                        property "state" (token "ERASED")
                        property "caseReference" reference
                        property "revision" revision
                    ]
            ]

    let summary =
        Schema.oneOf
            [
                objectOf
                    [
                        property "state" (token "LIVE")
                        property "caseReference" reference
                        property "revision" revision
                        property
                            "status"
                            ((CaseStatuses.all |> List.map CaseStatuses.token |> enumeration))
                        property "disposition" disposition
                    ]
                objectOf
                    [
                        property "state" (token "ERASED")
                        property "caseReference" reference
                        property "revision" revision
                    ]
            ]

    let receipt =
        objectOf
            [
                property "operationId" uuid
                property "position" (ref "Position")
                property "caseReference" reference
                property "revision" revision
                property "act" act
                property "disposition" (Schema.nullable disposition)
                property "snapshot" (Schema.nullable (ref "CaseView"))
                property "recordedAt" timestamp
                property "recordedBy" uuid
            ]

    let change =
        objectOf
            [
                property "operationId" uuid
                property "position" (ref "Position")
                property "revision" revision
                property "act" act
                property "recordedAt" timestamp
                property "recordedBy" uuid
            ]

    let history =
        Schema.oneOf
            [
                tagged "SUMMARY" [ property "change" (ref "ChangeSummary") ]
                tagged "FULL" [ property "receipt" (ref "Receipt") ]
            ]

    let review =
        objectOf
            [
                property "before" (Schema.nullable (ref "CaseView"))
                property "proposed" (ref "CaseView")
                property
                    "changes"
                    (array (
                        objectOf
                            [
                                property
                                    "fieldName"
                                    (SemanticContract.current.Fields
                                     |> List.map _.Name
                                     |> enumeration)
                                property "before" (Schema.nullable text)
                                property "after" (Schema.nullable text)
                            ]
                    ))
                property "context" (ref "RuntimeContext")
                property "advisory" (Schema.constant (BooleanConstant true))
            ]

    let rejection =
        objectOf
            [
                property "code" (WireTokens.rejectionCodes |> List.map snd |> enumeration)
                property "diagnostic" RejectionDiagnosticSchemas.value
                property "message" text
                property
                    "field"
                    (Schema.nullable (
                        enumeration ((InputTargets.all |> List.map snd) @ [ "limit"; "cursor" ])
                    ))
                property "actualRevision" (Schema.nullable revision)
                property
                    "recommendedAction"
                    (enumeration
                        [ "CORRECT_INPUT"; "READ_CURRENT"; "STOP_AND_INVESTIGATE"; "NONE_REQUIRED" ])
            ]

    let fault =
        CoreFaults.all
        |> List.map (fun (reason, id) ->
            objectOf
                [
                    property "code" (token (WireTokens.faultCode reason.Code))
                    property
                        "diagnostic"
                        (objectOf [ property "id" (token id); property "parameters" (objectOf []) ])
                    property "message" text
                    property "recommendedAction" (token (WireTokens.action reason.Action))
                ])
        |> Schema.oneOf

    let definitions =
        [
            "CaseFields", fields
            "CaseView", view
            "Position", position
            "Receipt", receipt
            "ChangeSummary", change
            "HistoryEntry", history
            "Review", review
            "Rejection", rejection
            "Fault", fault
            "RuntimeContext", context
            "SemanticDefinition",
            (ProjectionSchema.definitionDocument SemanticContract.current).Root
        ]
