namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open System
open System.Text.Json

module CorrelationEvaluation =
    let rec private memberAt path (value: JsonElement) =
        match path with
        | [] -> Some value
        | name :: rest when value.ValueKind = JsonValueKind.Object ->
            match value.TryGetProperty(name: string) with
            | true, child -> memberAt rest child
            | _ -> None
        | _ -> None

    let private compare comparison left right =
        match comparison, left, right with
        | CorrelationComparison.Exact, Some left, Some right -> JsonElement.DeepEquals(left, right)
        | CorrelationComparison.AsciiReference, Some left, Some right when
            left.ValueKind = JsonValueKind.String && right.ValueKind = JsonValueKind.String
            ->
            let left = left.GetString() |> nonNull
            let right = right.GetString() |> nonNull

            let valid (value: string) =
                value.Length >= 1
                && value.Length <= 80
                && value |> Seq.forall (fun character -> character >= '!' && character <= '~')

            valid left
            && valid right
            && String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        | CorrelationComparison.Successor, Some left, Some right when
            left.ValueKind = JsonValueKind.Number && right.ValueKind = JsonValueKind.Number
            ->
            match left.TryGetInt64(), right.TryGetInt64() with
            | (true, expected), (true, actual) ->
                expected >= 0L && expected < JsonValue.MaximumInteger && actual = expected + 1L
            | _ -> false
        | _ -> false

    let matchesUsing rules endpoint (request: JsonElement) (reply: JsonElement) =
        let outcome = memberAt [ "outcome"; "tag" ] reply

        let tag =
            outcome
            |> Option.bind (fun value ->
                if value.ValueKind = JsonValueKind.String then
                    Some(value.GetString() |> nonNull)
                else
                    None)

        rules
        |> List.tryFind (fun rule -> rule.Endpoint = endpoint && Some rule.Outcome = tag)
        |> Option.exists (fun rule ->
            let scope = memberAt [ "outcome"; "data"; "scope" ] reply

            let scopeMatches =
                rule.Scope
                |> Option.forall (fun expected ->
                    scope
                    |> Option.exists (fun value ->
                        value.ValueKind = JsonValueKind.String && value.GetString() = expected))

            scopeMatches
            && rule.Bindings
               |> List.forall (fun binding ->
                   compare
                       binding.Comparison
                       (memberAt binding.RequestPath request)
                       (memberAt binding.ReplyPath reply)))

    let matches endpoint request reply =
        matchesUsing CorrelationBindings.all endpoint request reply
