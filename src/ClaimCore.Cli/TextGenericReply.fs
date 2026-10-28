namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextGenericReply =
    open TextReplyFormat

    let private memberLabel language name =
        let templates = MessageCatalogs.templates language

        [ "field." + name + ".label"; "label." + name ]
        |> List.tryFind (fun key -> Map.containsKey key templates)
        |> Option.map (label language)
        |> Option.defaultValue name

    let rec private resolve definitions value =
        function
        | Schema.Ref name -> resolve definitions value (Map.find name definitions)
        | Schema.Vocabulary(_, schema) -> resolve definitions value schema
        | Schema.OneOf(variants, _) ->
            let matches =
                function
                | Schema.Object(fields, _) ->
                    fields
                    |> List.forall (fun (name, schema) ->
                        match schema with
                        | Schema.Const token ->
                            JsonValue.tryMember name value = Some(JsonValue.String token)
                        | _ -> true)
                | Schema.Null -> value = JsonValue.Null
                | _ -> true

            variants
            |> List.tryFind matches
            |> Option.defaultWith (fun () ->
                invalidOp "Admitted output requires a matching schema.")
            |> resolve definitions value
        | schema -> schema

    let rec private structured definitions =
        function
        | Schema.Ref name -> structured definitions (Map.find name definitions)
        | Schema.Vocabulary(_, schema) -> structured definitions schema
        | Schema.Object _ -> true
        | Schema.OneOf(variants, _) -> variants |> List.exists (structured definitions)
        | _ -> false

    let private rank definitions value schema =
        match resolve definitions value schema with
        | Schema.Array(items, _, _) -> if structured definitions items then 1 else 2
        | Schema.Object _ -> 1
        | _ -> 0

    let private scalar language =
        function
        | JsonValue.Null -> "—"
        | JsonValue.String text -> displayed language (Some text)
        | value -> value |> WireWriter.bytes |> Encoding.UTF8.GetString

    let rec private lines language definitions schema value =
        match value, resolve definitions value schema with
        | JsonValue.Object fields, Schema.Object(properties, _) ->
            properties
            |> List.choose (fun (name, schema) ->
                fields
                |> List.tryFind (fst >> (=) name)
                |> Option.map (fun (_, value) -> name, schema, value))
            |> List.sortBy (fun (_, schema, value) -> rank definitions value schema)
            |> List.collect (fun (name, schema, value) ->
                match value with
                | JsonValue.Object _ -> lines language definitions schema value
                | _ -> [ memberLabel language name + ": " + scalar language value ])
        | _ -> [ scalar language value ]

    let render language (reply: ServiceReply) =
        let normalized =
            reply.Route.NormalizeOutput reply.Wire
            |> Result.defaultWith (fun _ -> invalidOp "An admitted reply must remain valid.")

        let outcome = JsonValue.tryMember "outcome" normalized |> Option.get
        let data = JsonValue.tryMember "data" outcome |> Option.get

        match reply.Class.Kind with
        | ReplyKind.Rejected ->
            DiagnosticCodecs.diagnostic.Read "$.outcome.data" data
            |> Result.defaultWith (fun _ -> invalidOp "An admitted refusal must remain valid.")
            |> TextDiagnosticReply.refusalLines language
        | ReplyKind.Failed ->
            DiagnosticCodecs.fault.Read "$.outcome.data" data
            |> Result.defaultWith (fun _ -> invalidOp "An admitted fault must remain valid.")
            |> TextDiagnosticReply.faultLines language
        | ReplyKind.OutcomeUnknown ->
            [ label language (CliMessages.key CliMessage.UncertainDelivery) ]
        | _ ->
            let definitions = reply.Route.OutputDefinitions

            let memberSchema name schema value =
                match resolve definitions value schema with
                | Schema.Object(fields, _) -> fields |> List.find (fst >> (=) name) |> snd
                | _ -> invalidOp "An admitted outcome must have an object schema."

            let outcomeSchema = memberSchema "outcome" reply.Route.OutputSchema normalized
            let dataSchema = memberSchema "data" outcomeSchema outcome
            lines language definitions dataSchema data
