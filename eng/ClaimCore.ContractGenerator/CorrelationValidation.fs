namespace ClaimCore.ContractGeneration

open ClaimCore.Wire
open System.Reflection
open System.Text.Json

/// Reject stale or contradictory binding paths against the codec-owned shapes before publication.
module CorrelationValidation =
    let rec private resolve definitions =
        function
        | Schema.Ref name -> resolve definitions (Map.find name definitions)
        | schema -> schema

    let rec private at definitions path schema =
        match path, resolve definitions schema with
        | [], value -> [ value ]
        | _, Schema.OneOf(variants, _) -> variants |> List.collect (at definitions path)
        | name :: rest, Schema.Object(fields, _) ->
            fields
            |> List.tryFind (fst >> (=) name)
            |> Option.map (snd >> at definitions rest)
            |> Option.defaultValue []
        | _ -> []

    let rec private scalar definitions comparison schema =
        match comparison, resolve definitions schema with
        | CorrelationComparison.Successor, Schema.Integer _ -> true
        | CorrelationComparison.Exact, Schema.String _
        | CorrelationComparison.Exact, Schema.Const _
        | CorrelationComparison.Exact, Schema.Integer _
        | CorrelationComparison.Exact, Schema.Boolean
        | CorrelationComparison.AsciiReference, Schema.String _ -> true
        | _, Schema.OneOf(variants, _) -> variants |> List.forall (scalar definitions comparison)
        | _ -> false

    let private response rule =
        let schema, definitions = Replies.schema rule.Endpoint
        // The identity oracle also projects ACCEPTED receipts for preview. Wire still admits only ALREADY_ACCEPTED.
        let tag =
            if rule.Endpoint = Endpoint.CasePreview && rule.Outcome = "ACCEPTED" then
                "ALREADY_ACCEPTED"
            else
                rule.Outcome

        let variants =
            at definitions [ "outcome" ] schema
            |> List.collect (fun schema ->
                match resolve definitions schema with
                | Schema.OneOf(variants, _) -> variants
                | value -> [ value ])

        let selected =
            variants
            |> List.filter (fun variant -> at definitions [ "tag" ] variant = [ Schema.Const tag ])

        match selected with
        | [ outcome ] ->
            (match resolve definitions schema with
             | Schema.Object(fields, optional) ->
                 Schema.Object(
                     fields
                     |> List.map (fun (name, value) ->
                         name, if name = "outcome" then outcome else value),
                     optional
                 )
             | _ -> invalidOp "Correlation reply has no envelope object."),
            definitions
        | _ -> invalidOp "Correlation outcome is missing or ambiguous in the codec."

    let rec private constants definitions schema =
        match resolve definitions schema with
        | Schema.Const value -> [ value ]
        | Schema.OneOf(variants, _) -> variants |> List.collect (constants definitions)
        | _ -> []

    let private reference definitions schema =
        match resolve definitions schema with
        | Schema.String _ as value -> value = ResponseScalars.reference.Schema
        | _ -> false

    let private evidence rules =
        use stream =
            Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream("ClaimCore.Oracles.correlation-vectors.json")
            |> nonNull

        use document = JsonDocument.Parse stream

        for sample in document.RootElement.GetProperty("cases").EnumerateArray() do
            let endpoint =
                Endpoint.tryParse (sample.GetProperty("endpoint").GetString() |> nonNull)
                |> Option.get

            let actual =
                CorrelationEvaluation.matchesUsing
                    rules
                    endpoint
                    (sample.GetProperty "request")
                    (sample.GetProperty "reply")

            if actual <> sample.GetProperty("correlated").GetBoolean() then
                invalidOp "Correlation specification contradicts independent identity evidence."

    let private validateRule (rule: CorrelationRule) =
        let request, requestDefinitions = Admission.schema rule.Endpoint
        let reply, replyDefinitions = response rule

        for binding in rule.Bindings do
            let left = at requestDefinitions binding.RequestPath request
            let right = at replyDefinitions binding.ReplyPath reply

            if
                List.isEmpty left
                || List.isEmpty right
                || not (List.forall (scalar requestDefinitions binding.Comparison) left)
                || not (List.forall (scalar replyDefinitions binding.Comparison) right)
                || (binding.Comparison = CorrelationComparison.AsciiReference
                    && not (List.forall (reference replyDefinitions) right))
            then
                invalidOp "Correlation binding is absent or has an incompatible scalar shape."

        match rule.Scope with
        | Some scope when
            (at replyDefinitions [ "outcome"; "data"; "scope" ] reply
             |> List.collect (constants replyDefinitions))
            <> [ scope ]
            ->
            invalidOp "Correlation scope contradicts the codec."
        | _ -> ()

    let validate rules =
        let keys = rules |> List.map (fun rule -> rule.Endpoint, rule.Outcome)

        let expected =
            CorrelationBindings.all
            |> List.map (fun rule -> rule.Endpoint, rule.Outcome)
            |> Set.ofList

        if keys.Length <> (Set.ofList keys).Count || Set.ofList keys <> expected then
            invalidOp "Correlation specification is duplicate or incomplete."

        rules |> List.iter validateRule
        evidence rules
