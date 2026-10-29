module ClaimCore.Tests.GenericReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.TestSupport

let private authorityReview () =
    let reply: PreviewReply =
        {
            OperationId = Guid.Parse "10000000-0000-4000-8000-000000000001"
            Outcome =
                Outcome.Succeeded(
                    Previewed.Reviewed(
                        OperationReview.Authority
                            {
                                Target = None
                                Before = { Enabled = false; Grants = [] }
                                After =
                                    {
                                        Enabled = true
                                        Grants =
                                            [
                                                {
                                                    Role = Role.CaseReader
                                                    Scope = GrantTarget.Installation
                                                }
                                            ]
                                    }
                            }
                    )
                )
        }

    CliReplies.ofValue Api.operationPreview reply

let rec private unknownAuthority =
    function
    | JsonValue.String "AUTHORITY" -> JsonValue.String "UNDECLARED"
    | JsonValue.Object fields ->
        fields
        |> List.map (fun (name, value) -> name, unknownAuthority value)
        |> JsonValue.Object
    | JsonValue.Array values -> values |> List.map unknownAuthority |> JsonValue.Array
    | value -> value

[<Tests>]
let tests =
    testList
        "generic reply schema selection"
        [
            testCase
                "non-first referenced tagged object keeps target and before after facts"
                (fun () ->
                    let lines = TextGenericReply.render "en" (authorityReview ())
                    Expect.contains lines "kind: AUTHORITY" "The non-first union tag survives"

                    Expect.contains
                        lines
                        "target: —"
                        "The authority target is not omitted by the CASE schema"

                    Expect.contains lines "Enabled: false" "The before fact survives"
                    Expect.contains lines "Enabled: true" "The after fact survives"

                    Expect.isTrue
                        (lines
                         |> List.exists (fun line ->
                             line.Contains("CASE_READER", StringComparison.Ordinal)))
                        "The after grant facts survive")
            testCase "unknown tagged object remains rejected before generic walking" (fun () ->
                let original = authorityReview ()

                let invalid =
                    { original with
                        Wire = unknownAuthority original.Wire
                    }

                Expect.isError
                    (invalid.Route.NormalizeOutput invalid.Wire)
                    "The real codec rejects the unknown tag"

                Expect.throws
                    (fun () -> TextGenericReply.render "en" invalid |> ignore)
                    "The generic renderer cannot silently accept an unknown alternative")
        ]
