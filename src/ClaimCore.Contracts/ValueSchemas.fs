namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open WireSchema

module internal ValueSchemas =
    let private ref = Schema.reference

    let context =
        objectOf
            [
                property "productVersion" text
                property
                    "businessDate"
                    (ScalarSchemas.scalar (FieldDefinitions.scalar "incidentDate"))
                property "timeZone" text
            ]

    let operationOffer =
        objectOf
            [
                property "definition" text
                property "caseReference" (Schema.nullable reference)
                property "expectedRevision" (Schema.nullable revision)
                property
                    "choices"
                    (array (objectOf [ property "input" text; property "values" (array text) ]))
            ]

    let actorContext =
        objectOf
            [
                property "productVersion" text
                property
                    "businessDate"
                    (ScalarSchemas.scalar (FieldDefinitions.scalar "incidentDate"))
                property "timeZone" text
                property "offers" (array operationOffer)
                property
                    "capabilities"
                    (array (Capabilities.all |> List.map Capabilities.token |> enumeration))
            ]

    let fields =
        FieldDefinitions.all
        |> List.map (fun field ->
            let schema = ScalarSchemas.scalar field.Scalar

            property
                field.Name
                (if field.AllowsAbsence then
                     Schema.nullable schema
                 else
                     schema))
        |> objectOf

    let private live available =
        tagged
            "LIVE"
            ([ property "disposition" disposition; property "fields" (ref "CaseFields") ]
             @ (if available then
                    [ property "availableActs" (array command) ]
                else
                    []))

    let private state available =
        Schema.oneOf [ live available; tagged "ERASED" [] ]

    let private view available =
        objectOf
            [
                property "caseReference" reference
                property "revision" sequence
                property "state" (state available)
                if available then
                    property "offers" (array operationOffer)
                if available then
                    property "historyDetails" (array (enumeration [ "SUMMARY"; "FULL" ]))
            ]

    let record = view true
    let snapshot = view false

    let position =
        objectOf [ property "sequence" sequence; property "entryHash" digest ]

    let summary =
        Schema.oneOf
            [
                objectOf
                    [
                        property "state" (token "LIVE")
                        property "caseReference" reference
                        property "revision" sequence
                        property
                            "status"
                            (CaseStatuses.all |> List.map CaseStatuses.token |> enumeration)
                        property "disposition" disposition
                    ]
                objectOf
                    [
                        property "state" (token "ERASED")
                        property "caseReference" reference
                        property "revision" sequence
                    ]
            ]

    let private receiptVariants =
        let common =
            [
                property "operationId" uuid
                property "position" (ref "Position")
                property "recordedAt" timestamp
                property "recordedBy" uuid
            ]

        let disclosure =
            Schema.oneOf
                [
                    tagged "DISCLOSED" [ property "state" (live false) ]
                    tagged "WITHHELD" []
                    tagged "ERASED" []
                ]

        [
            objectOf (
                common
                @ [
                    property "scope" (token "CASE")
                    property "caseReference" reference
                    property "revision" sequence
                    property "act" command
                    property "snapshot" disclosure
                ]
            )
            objectOf (
                common
                @ [
                    property "scope" (token "AUTHORITY")
                    property "targetActorId" uuid
                    property
                        "act"
                        (RecordedAuthorityActs.all
                         |> List.map RecordedAuthorityActs.token
                         |> enumeration)
                ]
            )
        ]

    let caseReceipt = List.head receiptVariants
    let authorityReceipt = List.last receiptVariants
    let receipt = Schema.oneOf receiptVariants

    let change =
        objectOf
            [
                property "operationId" uuid
                property "position" (ref "Position")
                property "revision" sequence
                property "act" command
                property "recordedAt" timestamp
                property "recordedBy" uuid
            ]

    let summaryHistory = tagged "SUMMARY" [ property "change" (ref "ChangeSummary") ]
    let fullHistory = tagged "FULL" [ property "receipt" (ref "CaseReceipt") ]
    let history = Schema.oneOf [ summaryHistory; fullHistory ]

    let review =
        objectOf
            [
                property "before" (Schema.nullable snapshot)
                property "after" snapshot
                property
                    "changes"
                    (array (
                        objectOf
                            [
                                property
                                    "field"
                                    (FieldDefinitions.all |> List.map _.Name |> enumeration)
                                property "before" (Schema.nullable text)
                                property "after" (Schema.nullable text)
                            ]
                    ))
                property "context" context
            ]

    let rejection =
        objectOf
            [
                property "diagnostic" RejectionDiagnosticSchemas.value
                property "message" text
                property
                    "field"
                    (Schema.nullable (
                        enumeration ((InputTargets.all |> List.map snd) @ [ "limit"; "cursor" ])
                    ))
                property "actualRevision" (Schema.nullable sequence)
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
                    property
                        "diagnostic"
                        (objectOf [ property "id" (token id); property "parameters" (objectOf []) ])
                    property "message" text
                    property "recommendedAction" (token (WireTokens.action reason.Action))
                ])
        |> Schema.oneOf

    let actor =
        objectOf
            [
                property "actorId" uuid
                property "principal" RequestSchemas.principal
                property "enabled" Schema.boolean
            ]

    let grant =
        objectOf
            [
                property "role" (Roles.all |> List.map Roles.token |> enumeration)
                property "scope" RequestSchemas.scope
            ]

    let definitions =
        [
            "CaseFields", fields
            "CaseView", record
            "CaseSnapshot", snapshot
            "Position", position
            "CaseReceipt", caseReceipt
            "AuthorityReceipt", authorityReceipt
            "Receipt", receipt
            "ChangeSummary", change
            "HistoryEntry", history
            "SummaryHistoryEntry", summaryHistory
            "FullHistoryEntry", fullHistory
            "Review", review
            "Rejection", rejection
            "Fault", fault
            "RuntimeContext", context
            "ActorContext", actorContext
            "ActorSummary", actor
            "AuthorityGrant", grant
        ]
