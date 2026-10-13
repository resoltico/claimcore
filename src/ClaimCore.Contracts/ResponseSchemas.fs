namespace ClaimCore.Contracts

open WireSchema

module internal ResponseSchemas =
    let private ref = Schema.reference
    let private result tag data = tagged tag [ property "data" data ]

    let private refusal =
        [
            result "REJECTED" (ref "Rejection")
            result "FAILED" (ref "Fault")
            result "CANCELLED" Schema.nullValue
        ]

    let private query data =
        Schema.oneOf (result "SUCCEEDED" data :: refusal)

    let private lookup name value identity identitySchema =
        Schema.oneOf
            [
                tagged "FOUND" [ property name value ]
                tagged "NOT_FOUND" [ property identity identitySchema ]
            ]

    let private execute =
        Schema.oneOf (
            [
                result "ACCEPTED" (ref "Receipt")
                result "ALREADY_ACCEPTED" (ref "Receipt")
                result
                    "OUTCOME_UNKNOWN"
                    (objectOf [ property "operationId" uuid; property "requestSha256" digest ])
            ]
            @ refusal
        )

    let private authority observe =
        let applied =
            objectOf
                [
                    property "eventId" uuid
                    property "position" (ref "Position")
                    property "targetActorId" uuid
                ]

        Schema.oneOf (
            [ result "APPLIED" applied; result "ALREADY_APPLIED" applied ]
            @ refusal
            @ (if observe then
                   []
               else
                   [ result "OUTCOME_UNKNOWN" (objectOf [ property "eventId" uuid ]) ])
        )

    let private schemas =
        [
            "session",
            (result
                "SUCCEEDED"
                (objectOf
                    [
                        property "authenticated" Schema.boolean
                        property "wireFingerprint" digest
                        property "antiforgeryToken" (Schema.nullable text)
                    ]))
            "session.logout", (result "SUCCEEDED" Schema.nullValue)
            "definition",
            (query (
                objectOf
                    [
                        property "semanticFingerprint" digest
                        property "wireFingerprint" digest
                        property "runtime" (ref "RuntimeContext")
                        property "definition" (ref "SemanticDefinition")
                    ]
            ))
            "case.get", (query (lookup "current" ValueSchemas.record "caseReference" reference))
            "case.list",
            (query (
                objectOf
                    [
                        property "items" (array ValueSchemas.summary)
                        property "nextCursor" (Schema.nullable text)
                        property "availableCommands" (array command)
                    ]
            ))
            "case.history",
            (query (
                Schema.oneOf
                    [
                        tagged
                            "FOUND"
                            [
                                property "entries" (array (ref "HistoryEntry"))
                                property "nextCursor" (Schema.nullable text)
                            ]
                        tagged "NOT_FOUND" [ property "caseReference" reference ]
                    ]
            ))
            "operation.observe", (query (lookup "receipt" (ref "Receipt") "operationId" uuid))
            "command.preview",
            (Schema.oneOf (
                [
                    result
                        "REVIEWED"
                        (objectOf
                            [ property "review" (ref "Review"); property "requestSha256" digest ])
                    result "ALREADY_ACCEPTED" (ref "Receipt")
                ]
                @ refusal
            ))
            "command.execute", (execute)
            "case.steward", (execute)
            "authority.observe", (authority true)
            "authority.register", (authority false)
            "authority.setGrant", (authority false)
            "authority.setEnabled", (authority false)
        ]
        |> Map.ofList

    let outcome identifier =
        Map.tryFind identifier schemas
        |> Option.defaultWith (fun () -> invalidArg "endpoint" "Unknown endpoint.")

    let envelope endpoint =
        objectOf [ property "endpoint" (token endpoint); property "outcome" (outcome endpoint) ]
