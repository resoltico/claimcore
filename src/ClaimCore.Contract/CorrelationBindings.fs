namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

[<RequireQualifiedAccess>]
type CorrelationComparison =
    | Exact
    | AsciiReference
    | Successor

type CorrelationBinding =
    {
        RequestPath: string list
        ReplyPath: string list
        Comparison: CorrelationComparison
    }

type CorrelationRule =
    {
        Endpoint: Endpoint
        Outcome: string
        Scope: string option
        Bindings: CorrelationBinding list
    }

/// Finite identity bindings. Shape admission precedes these comparisons.
module CorrelationBindings =
    let private exact request reply =
        {
            RequestPath = request
            ReplyPath = reply
            Comparison = CorrelationComparison.Exact
        }

    let private operation = exact [ "operationId" ] [ "operationId" ]
    let private data path = [ "outcome"; "data" ] @ path

    let private receipt endpoint =
        [
            yield exact [ "operationId" ] (data [ "operationId" ])
            yield exact [ "act"; "kind" ] (data [ "act" ])
            if Endpoint.receiptScope endpoint = Some ReceiptScope.Case then
                yield
                    {
                        RequestPath = [ "caseReference" ]
                        ReplyPath = data [ "caseReference" ]
                        Comparison = CorrelationComparison.AsciiReference
                    }

                yield
                    {
                        RequestPath = [ "expectedRevision" ]
                        ReplyPath = data [ "revision" ]
                        Comparison = CorrelationComparison.Successor
                    }
        ]

    let private outcomeBindings endpoint =
        function
        | "ACCEPTED"
        | "ALREADY_ACCEPTED" -> receipt endpoint
        | "OUTCOME_UNKNOWN" -> [ exact [ "operationId" ] (data [ "operationId" ]) ]
        | "REVIEWED" ->
            [
                {
                    RequestPath = [ "caseReference" ]
                    ReplyPath = data [ "review"; "after"; "caseReference" ]
                    Comparison = CorrelationComparison.AsciiReference
                }
                {
                    RequestPath = [ "expectedRevision" ]
                    ReplyPath = data [ "review"; "after"; "revision" ]
                    Comparison = CorrelationComparison.Successor
                }
            ]
        | "SUCCEEDED" when endpoint = Endpoint.AuthorityGrants ->
            [ exact [ "actorId" ] (data [ "actor"; "actorId" ]) ]
        | "SUCCEEDED" when endpoint = Endpoint.OperationObserve ->
            [ exact [ "operationId" ] (data [ "operationId" ]) ]
        | _ -> []

    let rec private resolve definitions =
        function
        | Schema.Ref name -> resolve definitions (Map.find name definitions)
        | schema -> schema

    let private properties definitions schema =
        match resolve definitions schema with
        | Schema.Object(fields, _) -> fields
        | _ -> invalidOp "Correlation requires an object codec."

    let private outcomes endpoint =
        let schema, definitions = Replies.schema endpoint

        let outcome =
            properties definitions schema |> List.find (fst >> (=) "outcome") |> snd

        let variants =
            match resolve definitions outcome with
            | Schema.OneOf(variants, _) -> variants
            | value -> [ value ]

        variants
        |> List.map (fun variant ->
            match properties definitions variant |> List.find (fst >> (=) "tag") |> snd with
            | Schema.Const tag -> tag
            | _ -> invalidOp "Correlation outcome has no constant tag.")

    let all =
        [
            for endpoint in Endpoint.all do
                for outcome in
                    (outcomes endpoint
                     @ (if endpoint = Endpoint.CasePreview then
                            [ "ACCEPTED" ]
                        else
                            [])) do
                    let scope =
                        if outcome = "ACCEPTED" || outcome = "ALREADY_ACCEPTED" then
                            Endpoint.receiptScope endpoint
                            |> Option.map (function
                                | ReceiptScope.Case -> "CASE"
                                | ReceiptScope.Authority -> "AUTHORITY")
                        else
                            None

                    yield
                        {
                            Endpoint = endpoint
                            Outcome = outcome
                            Scope = scope
                            Bindings =
                                (if Endpoint.receiptScope endpoint <> None then
                                     [ operation ]
                                 else
                                     [])
                                @ outcomeBindings endpoint outcome
                        }
        ]
