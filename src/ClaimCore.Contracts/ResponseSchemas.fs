namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

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

    let private execute receipt =
        Schema.oneOf (
            [
                result "ACCEPTED" (ref receipt)
                result "ALREADY_ACCEPTED" (ref receipt)
                result
                    "OUTCOME_UNKNOWN"
                    (objectOf [ property "operationId" uuid; property "requestSha256" digest ])
            ]
            @ refusal
        )

    let private page name schema =
        query (
            objectOf [ property name (array schema); property "nextCursor" (Schema.nullable text) ]
        )

    let outcome =
        function
        | "session" ->
            result
                "SUCCEEDED"
                (objectOf
                    [
                        property "authenticated" Schema.boolean
                        property "wireFingerprint" digest
                        property "antiforgeryToken" (Schema.nullable text)
                    ])
        | "session.logout" -> result "SUCCEEDED" Schema.nullValue
        | "context" -> query (ref "ActorContext")
        | "case.get" -> query (ref "CaseView")
        | "case.list" -> page "items" ValueSchemas.summary
        | "case.history" -> page "entries" (ref "HistoryEntry")
        | "operation.observe" -> query (ref "Receipt")
        | "authority.list" -> page "items" (ref "ActorReview")
        | "case.preview" ->
            Schema.oneOf (
                [
                    result
                        "REVIEWED"
                        (objectOf
                            [ property "review" (ref "Review"); property "requestSha256" digest ])
                    result "ALREADY_ACCEPTED" (ref "CaseReceipt")
                ]
                @ refusal
            )
        | "case.execute" -> execute "CaseReceipt"
        | "authority.execute" -> execute "AuthorityReceipt"
        | _ -> invalidArg "endpoint" "Unknown endpoint."

    let envelope endpoint =
        objectOf
            [
                property "endpoint" (token endpoint)
                if
                    endpoint = "case.preview"
                    || endpoint = "case.execute"
                    || endpoint = "authority.execute"
                then
                    property "operationId" uuid
                property "outcome" (outcome endpoint)
            ]

    let receiptScope endpoint =
        let rec scopes schema =
            match schema with
            | ReferenceSchema "CaseReceipt" -> ["CASE"]
            | ReferenceSchema "AuthorityReceipt" -> ["AUTHORITY"]
            | ObjectSchema value -> value.Properties |> List.collect (fun property -> scopes property.Schema)
            | OneOfSchema alternatives -> alternatives |> List.collect scopes
            | _ -> []
        match outcome endpoint |> scopes |> List.distinct with
        | [scope] -> scope
        | [] -> "NONE"
        | _ -> invalidArg "endpoint" "Receipt scope is ambiguous."
