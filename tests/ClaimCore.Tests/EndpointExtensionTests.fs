module ClaimCore.Tests.EndpointExtensionTests

open System.Text
open Expecto
open ClaimCore.Contracts

let private query identifier : WebEndpoint =
    {
        Identifier = identifier
        Method = "POST"
        Path = "/api/v3/cases/count"
        Body = Some(JsonBody(Schema.objectOf false []))
        Response =
            Schema.objectOf false [ Schema.property "count" (Schema.integer (Some 0L) None) true ]
        SuccessMediaType = None
    }

let private extend endpoint =
    let current = ContractProjection.current ()

    { current with
        WebEndpoints = current.WebEndpoints @ [ endpoint ]
    }

let private countQuery () =
    let artifacts = WebTypeScript.artifacts (extend (query "case.count"))

    let owners =
        artifacts
        |> List.filter (fun (name, bytes) ->
            name.StartsWith("web-v3.types.responses.")
            && Encoding.UTF8.GetString(bytes).Contains("\"case.count\":"))
        |> List.map fst

    Expect.equal
        owners
        [ "web-v3.types.responses.read.ts" ]
        "A new case query has one response-file owner without another identifier inventory"

    Expect.isTrue
        (CliMutationCatalog.isMutation "case.count")
        "Response-file ownership cannot grant unreviewed read-only delivery semantics"

let private unknownFamily () =
    Expect.throwsT<System.InvalidOperationException>
        (fun () -> WebTypeScript.artifacts (extend (query "unreviewed.count")) |> ignore)
        "An unknown response family requires an explicit generator decision"

let private duplicateIdentifier () =
    let current = ContractProjection.current ()

    let duplicate =
        { current with
            WebEndpoints = current.WebEndpoints @ [ current.WebEndpoints.Head ]
        }

    Expect.throwsT<System.InvalidOperationException>
        (fun () -> WebTypeScript.artifacts duplicate |> ignore)
        "Sets cannot hide duplicate endpoint declarations"

let tests =
    testList
        "endpoint extension ownership"
        [
            testCase
                "[CC-CLI-003] new case query derives response ownership without mutation authority"
                countQuery
            testCase
                "[CC-CLI-001] generated responses refuse an unreviewed endpoint family"
                unknownFamily
            testCase
                "[CC-CLI-001] generated responses refuse duplicate endpoint identifiers"
                duplicateIdentifier
        ]
