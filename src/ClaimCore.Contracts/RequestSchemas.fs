namespace ClaimCore.Contracts

open ClaimCore.Application
open WireSchema

module internal RequestSchemas =
    let empty = objectOf []

    let principal =
        objectOf
            [
                property "kind" (enumeration [ "HUMAN"; "SERVICE" ])
                property "issuer" (Schema.string (Some "uri") (Some "^https://") (Some 1) None)
                property "subject" text
            ]

    let scope =
        Schema.oneOf
            [
                objectOf [ property "type" (token "INSTALLATION") ]
                objectOf [ property "type" (token "CASE"); property "caseReference" reference ]
            ]

    let event = property "eventId" uuid
    let target = property "principal" principal

    let page =
        [
            optional "cursor" text
            property
                "limit"
                (Schema.integer (Some 1L) (Some(int64 SemanticContract.current.MaximumPageSize)))
        ]

    let steward =
        let common =
            [
                property "operationId" uuid
                property "caseReference" reference
                property "expectedRevision" revision
            ]

        [ "VOID"; "REINSTATE"; "ERASE" ]
        |> List.map (fun action ->
            common
            @ [ property "action" (token action) ]
            @ (if action = "ERASE" then
                   []
               else
                   [ property "reason" (Schema.string None None (Some 1) (Some 200)) ])
            |> objectOf)
        |> Schema.oneOf

    let private schemas =
        [
            "session", (empty)
            "session.logout", (empty)
            "definition", (empty)
            "command.preview", (ProjectionSchema.commandDraft SemanticContract.current)
            "command.execute", (ProjectionSchema.commandDraft SemanticContract.current)
            "case.get", (objectOf [ property "caseReference" reference ])
            "case.list", (objectOf page)
            "case.history",
            (objectOf (
                [
                    property "caseReference" reference
                    property "detail" (enumeration [ "SUMMARY"; "FULL" ])
                ]
                @ page
            ))
            "operation.observe", (objectOf [ property "operationId" uuid ])
            "case.steward", (steward)
            "authority.register", (objectOf [ event; target ])
            "authority.setGrant",
            (objectOf
                [
                    event
                    target
                    property "role" (Roles.all |> List.map Roles.token |> enumeration)
                    property "scope" scope
                    property "active" Schema.boolean
                ])
            "authority.setEnabled", (objectOf [ event; target; property "enabled" Schema.boolean ])
            "authority.observe", (objectOf [ event ])
        ]
        |> Map.ofList

    let forEndpoint identifier =
        Map.tryFind identifier schemas
        |> Option.defaultWith (fun () -> invalidArg "endpoint" "Unknown endpoint.")
