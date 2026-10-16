namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open WireSchema

module internal RequestSchemas =
    let empty = objectOf []

    let principal =
        objectOf
            [
                property "kind" (enumeration [ "HUMAN"; "SERVICE" ])
                property "issuer" (Schema.string None None None None)
                property "subject" (Schema.string None None None None)
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

    let caseRequest =
        objectOf
            [
                property "operationId" uuid
                property "caseReference" reference
                property "expectedRevision" revision
                property
                    "act"
                    (CommandDefinitions.all
                     |> List.map CommandInputProjection.payload
                     |> Schema.oneOf)
            ]

    let authorityPrimitive =
        function
        | AuthorityInputPrimitive.Principal -> principal
        | AuthorityInputPrimitive.Role -> Roles.all |> List.map Roles.token |> enumeration
        | AuthorityInputPrimitive.Scope -> scope
        | AuthorityInputPrimitive.Boolean -> Schema.boolean

    let authorityRequest =
        let act =
            AuthorityOperationDefinitions.all
            |> List.map (fun definition ->
                objectOf (
                    property "kind" (token (RecordedAuthorityActs.token definition.Kind))
                    :: (definition.Inputs
                        |> List.map (fun input ->
                            property input.Name (authorityPrimitive input.Primitive)))
                ))
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
