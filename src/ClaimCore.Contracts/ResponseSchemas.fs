namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open WireSchema
open ClaimCore.Application

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

    let private bounded schema =
        Schema.array schema None (Some ServiceLimits.MaximumPageSize)

    let private page name schema =
        query (
            objectOf
                [ property name (bounded schema); property "nextCursor" (Schema.nullable text) ]
        )

    let private caseList =
        query (
            objectOf
                [
                    property "items" (bounded ValueSchemas.summary)
                    property "nextCursor" (Schema.nullable text)
                    property "offers" (array ValueSchemas.operationOffer)
                ]
        )

    let private history =
        let entries =
            Schema.oneOf
                [
                    Schema.array
                        (ref "SummaryHistoryEntry")
                        (Some 1)
                        (Some ServiceLimits.MaximumPageSize)
                    Schema.array
                        (ref "FullHistoryEntry")
                        (Some 1)
                        (Some ServiceLimits.MaximumFullHistoryPageSize)
                    Schema.array (ref "HistoryEntry") (Some 0) (Some 0)
                ]

        query (
            objectOf [ property "entries" entries; property "nextCursor" (Schema.nullable text) ]
        )

    let private grants =
        query (
            objectOf
                [
                    property "actor" (ref "ActorSummary")
                    property "items" (bounded (ref "AuthorityGrant"))
                    property "nextCursor" (Schema.nullable text)
                ]
        )

    let private queryOutcome =
        function
        | "context" -> Some(query (ref "ActorContext"))
        | "case.get" -> Some(query (ref "CaseView"))
        | "case.list" -> Some(caseList)
        | "case.history" -> Some(history)
        | "operation.observe" -> Some(query (ref "Receipt"))
        | "authority.list" -> Some(page "items" (ref "ActorSummary"))
        | "authority.grants" -> Some(grants)
        | _ -> None

    let private nonQueryOutcome =
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

    let outcome endpoint =
        match queryOutcome endpoint with
        | Some schema -> schema
        | None -> nonQueryOutcome endpoint

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
            | ReferenceSchema "CaseReceipt" -> [ "CASE" ]
            | ReferenceSchema "AuthorityReceipt" -> [ "AUTHORITY" ]
            | ObjectSchema value ->
                value.Properties |> List.collect (fun property -> scopes property.Schema)
            | OneOfSchema alternatives -> alternatives |> List.collect scopes
            | _ -> []

        match outcome endpoint |> scopes |> List.distinct with
        | [ scope ] -> scope
        | [] -> "NONE"
        | _ -> invalidArg "endpoint" "Receipt scope is ambiguous."
