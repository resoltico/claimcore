namespace ClaimCore.ClientProtocol

open System
open System.Text.Json

type CorrelationRule =
    {
        Selector: string
        Tags: string list
        ReplyPath: string list
        RequestPath: string list
        Comparison: string
    }

type CorrelationSelector =
    {
        Name: string
        ReceiptScopes: string list
        RequestPresent: string list list
        RequestAbsent: string list list
    }

/// Closed protocol comparisons and paths; both client runtimes interpret this finite table.
module CorrelationPolicy =
    let tagPath = [ "outcome"; "tag" ]

    let selectors =
        [
            {
                Name = "ALL"
                ReceiptScopes = []
                RequestPresent = []
                RequestAbsent = []
            }
            {
                Name = "RECEIPT_ENDPOINT"
                ReceiptScopes = [ "CASE"; "AUTHORITY" ]
                RequestPresent = []
                RequestAbsent = []
            }
            {
                Name = "CASE_RECEIPT"
                ReceiptScopes = [ "CASE" ]
                RequestPresent = []
                RequestAbsent = []
            }
            {
                Name = "ACTOR_QUERY"
                ReceiptScopes = []
                RequestPresent = [ [ "actorId" ] ]
                RequestAbsent = []
            }
            {
                Name = "OPERATION_QUERY"
                ReceiptScopes = []
                RequestPresent = [ [ "operationId" ] ]
                RequestAbsent = [ [ "act" ] ]
            }
        ]

    let private rule selector tags reply request comparison =
        {
            Selector = selector
            Tags = tags
            ReplyPath = reply
            RequestPath = request
            Comparison = comparison
        }

    let rules =
        let receipt = [ "ACCEPTED"; "ALREADY_ACCEPTED" ]

        [
            rule
                "ACTOR_QUERY"
                [ "SUCCEEDED" ]
                [ "outcome"; "data"; "actor"; "actorId" ]
                [ "actorId" ]
                "EXACT"
            rule "RECEIPT_ENDPOINT" [] [ "operationId" ] [ "operationId" ] "EXACT"
            rule "ALL" receipt [ "outcome"; "data"; "operationId" ] [ "operationId" ] "EXACT"
            rule "ALL" receipt [ "outcome"; "data"; "scope" ] [] "RECEIPT_SCOPE"
            rule "ALL" receipt [ "outcome"; "data"; "act" ] [ "act"; "kind" ] "EXACT"
            rule
                "CASE_RECEIPT"
                receipt
                [ "outcome"; "data"; "caseReference" ]
                [ "caseReference" ]
                "REFERENCE"
            rule
                "CASE_RECEIPT"
                receipt
                [ "outcome"; "data"; "revision" ]
                [ "expectedRevision" ]
                "SUCCESSOR"
            rule
                "ALL"
                [ "OUTCOME_UNKNOWN" ]
                [ "outcome"; "data"; "operationId" ]
                [ "operationId" ]
                "EXACT"
            rule
                "OPERATION_QUERY"
                [ "SUCCEEDED" ]
                [ "outcome"; "data"; "operationId" ]
                [ "operationId" ]
                "EXACT"
            rule
                "ALL"
                [ "REVIEWED" ]
                [ "outcome"; "data"; "review"; "after"; "caseReference" ]
                [ "caseReference" ]
                "REFERENCE"
            rule
                "ALL"
                [ "REVIEWED" ]
                [ "outcome"; "data"; "review"; "after"; "revision" ]
                [ "expectedRevision" ]
                "SUCCESSOR"
        ]

    let private path parts value =
        parts
        |> List.fold
            (fun state part ->
                state
                |> Option.bind (fun (element: JsonElement) ->
                    if element.ValueKind <> JsonValueKind.Object then
                        None
                    else
                        match element.TryGetProperty(part: string) with
                        | true, found -> Some found
                        | _ -> None))
            (Some value)

    let private text value =
        value
        |> Option.bind (fun (element: JsonElement) ->
            if element.ValueKind = JsonValueKind.String then
                element.GetString() |> Option.ofObj
            else
                None)

    let private compare scope request response rule =
        let left = path rule.ReplyPath response
        let right = path rule.RequestPath request

        match rule.Comparison with
        | "RECEIPT_SCOPE" -> text left = Some scope
        | "EXACT" ->
            match text left, text right with
            | Some left, Some right -> left = right
            | _ -> false
        | "REFERENCE" ->
            match text left, text right with
            | Some left, Some right ->
                String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            | _ -> false
        | "SUCCESSOR" ->
            match left, right with
            | Some left, Some right ->
                let mutable actual = 0L
                let mutable expected = 0L

                left.TryGetInt64(&actual)
                && right.TryGetInt64(&expected)
                && expected < 9007199254740991L
                && actual = expected + 1L
            | _ -> false
        | _ -> invalidOp "Unsupported correlation comparison."

    let evaluate receiptScope request response =
        let tag = path tagPath response |> text

        let selected rule =
            let selector =
                selectors |> List.find (fun selector -> selector.Name = rule.Selector)

            (selector.ReceiptScopes.IsEmpty
             || List.contains receiptScope selector.ReceiptScopes)
            && (selector.RequestPresent
                |> List.forall (fun parts -> path parts request |> Option.isSome))
            && (selector.RequestAbsent
                |> List.forall (fun parts -> path parts request |> Option.isNone))
            && (rule.Tags.IsEmpty
                || rule.Tags |> List.exists (fun expected -> tag = Some expected))

        rules
        |> List.filter selected
        |> List.forall (compare receiptScope request response)
