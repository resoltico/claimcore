namespace ClaimCore.ContractGeneration

open System.Text
open System.Text.Json.Nodes
open ClaimCore.Contracts

module Corpus =
    let private entry endpoint direction valid id bytes =
        let result = JsonObject()
        result.Add("id", JsonValue.Create<string> id)
        result.Add("endpoint", JsonValue.Create<string> endpoint)
        result.Add("direction", JsonValue.Create<string> direction)
        result.Add("valid", JsonValue.Create<bool> valid)
        result.Add("value", JsonNode.Parse(bytes: byte array))
        result

    let private admitted endpoint direction bytes =
        if direction = "request" then
            EndpointCatalog.admitRequest endpoint bytes |> Result.isOk
        else
            EndpointCatalog.admitResponse endpoint bytes |> Result.isOk

    let private cases endpoint direction document =
        SchemaSamples.variants document
        |> List.mapi (fun index bytes ->
            if not (admitted endpoint direction bytes) then
                invalidOp ("Invalid synthetic sample for " + endpoint + " " + direction)

            let invalid = JsonNode.Parse(bytes) |> nonNull
            invalid.AsObject().Add("unexpected", JsonValue.Create true)
            let invalidBytes = invalid.ToJsonString() |> Encoding.UTF8.GetBytes

            if admitted endpoint direction invalidBytes then
                invalidOp "Unknown-member negative control was admitted."

            [
                entry
                    endpoint
                    direction
                    true
                    (endpoint + "-" + direction + "-" + string index)
                    bytes
                entry
                    endpoint
                    direction
                    false
                    (endpoint + "-" + direction + "-unknown-" + string index)
                    invalidBytes
            ])
        |> List.concat

    let private referenceCases () =
        let accepted = [ "Demo-001"; String.replicate 80 "A"; "!~<>\"" ]

        let refused =
            [
                "🙂"
                "a\u200db"
                "a b"
                String.replicate 81 "🙂"
                "\u0085a"
                "a\u3000"
                "a\u0000b"
                ""
            ]

        [ true, accepted; false, refused ]
        |> List.collect (fun (valid, values) ->
            values
            |> List.mapi (fun index reference ->
                let node = JsonObject()
                node.Add("caseReference", JsonValue.Create reference)
                let bytes = node.ToJsonString() |> Encoding.UTF8.GetBytes

                if admitted "case.get" "request" bytes <> valid then
                    invalidOp "Reference corpus disagrees with its independent oracle."

                entry
                    "case.get"
                    "request"
                    valid
                    ("reference-" + string valid + "-" + string index)
                    bytes))

    let bytes () =
        let items = JsonArray()
        referenceCases () |> List.iter (fun value -> items.Add value)

        EndpointCatalog.all
        |> List.collect (fun e ->
            cases e.Identifier "request" e.Request
            @ cases e.Identifier "response" e.Response)
        |> List.iter (fun value -> items.Add(value))

        let result = JsonObject()
        result.Add("cases", items)
        result.ToJsonString() + "\n" |> Encoding.UTF8.GetBytes
