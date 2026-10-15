namespace ClaimCore.Contracts

open ClaimCore.Domain
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
                objectOf [ property "kind" (token "INSTALLATION") ]
                objectOf [ property "kind" (token "CASE"); property "caseReference" reference ]
            ]

    let page =
        [
            optional "cursor" (Schema.string None None (Some 1) (Some 512))
            property "limit" (Schema.integer (Some 1L) (Some(int64 ServiceLimits.MaximumPageSize)))
        ]

    let private stewardship =
        [ "VOID"; "REINSTATE"; "ERASE" ]
        |> List.map (fun kind ->
            objectOf (
                [ property "kind" (token kind) ]
                @ (if kind = "ERASE" then
                       []
                   else
                       [
                           property
                               "reason"
                               (FieldDefinitions.scalar "reason" |> ScalarSchemas.scalar)
                       ])
            ))

    let caseRequest =
        objectOf
            [
                property "operationId" uuid
                property "caseReference" reference
                property "expectedRevision" revision
                property
                    "act"
                    (Schema.oneOf (
                        (CommandDefinitions.all |> List.map CommandInputProjection.payload)
                        @ stewardship
                    ))
            ]

    let authorityRequest =
        let act =
            [
                objectOf [ property "kind" (token "REGISTER_ACTOR") ]
                objectOf
                    [ property "kind" (token "SET_ENABLED"); property "enabled" Schema.boolean ]
                objectOf
                    [
                        property "kind" (token "SET_GRANT")
                        property "role" (Roles.all |> List.map Roles.token |> enumeration)
                        property "scope" scope
                        property "active" Schema.boolean
                    ]
            ]
            |> Schema.oneOf

        objectOf [ property "operationId" uuid; property "target" principal; property "act" act ]

    let forEndpoint =
        function
        | "session"
        | "session.logout"
        | "context" -> empty
        | "case.preview"
        | "case.execute" -> caseRequest
        | "authority.execute" -> authorityRequest
        | "case.get" -> objectOf [ property "caseReference" reference ]
        | "case.list"
        | "authority.list" -> objectOf page
        | "case.history" ->
            objectOf (
                [
                    property "caseReference" reference
                    property "detail" (enumeration [ "SUMMARY"; "FULL" ])
                ]
                @ page
            )
        | "operation.observe" -> objectOf [ property "operationId" uuid ]
        | _ -> invalidArg "endpoint" "Unknown endpoint."
