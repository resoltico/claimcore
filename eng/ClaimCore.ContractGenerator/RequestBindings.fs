namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Catalog
open ClaimCore.Domain
open ClaimCore.Wire

/// Bind semantic inputs to codec members; unsupported shapes and incomplete inputs refuse generation.
module RequestBindings =
    let rec private resolve definitions =
        function
        | Schema.Ref name -> resolve definitions (Map.find name definitions)
        | schema -> schema

    let private properties definitions schema =
        match resolve definitions schema with
        | Schema.Object(fields, []) -> fields
        | _ -> invalidOp "Request bindings require a closed required object."

    let rec private leaves definitions prefix schema =
        match resolve definitions schema with
        | Schema.Const _ -> []
        | Schema.String _ -> [ List.last prefix, prefix ]
        | Schema.Object(fields, []) ->
            fields
            |> List.collect (fun (name, child) -> leaves definitions (prefix @ [ name ]) child)
        | _ -> invalidOp "Unsupported case request binding primitive."

    let private fields definitions prefix schema inputs =
        let actual = leaves definitions prefix schema
        let names = inputs |> List.map (fun input -> InputField.token input.FieldName)

        if List.sort names <> (actual |> List.map fst |> List.sort) then
            invalidOp "Semantic inputs and codec request members differ."

        actual |> List.map (fun (name, path) -> {| fieldName = name; path = path |})

    let private groups definitions schema definitionsOfGroups =
        let actual = properties definitions schema

        if
            (actual |> List.map fst |> List.sort)
            <> (definitionsOfGroups
                |> List.map (fun group -> CorrectionGroup.token group.Name)
                |> List.sort)
        then
            invalidOp "Correction group bindings are incomplete."

        definitionsOfGroups
        |> List.map (fun group ->
            let name = CorrectionGroup.token group.Name

            let variants =
                match resolve definitions (List.find (fst >> (=) name) actual |> snd) with
                | Schema.OneOf(variants, _) -> variants |> List.map (properties definitions)
                | _ -> invalidOp "Correction binding requires tagged choices."

            let choices =
                variants
                |> List.map (fun fields ->
                    match List.find (fst >> (=) "mode") fields |> snd with
                    | Schema.Const mode -> mode
                    | _ -> invalidOp "Correction choice has no constant mode.")

            let expected =
                group.Actions
                |> List.map (function
                    | CorrectionGroupAction.Keep -> "KEEP"
                    | CorrectionGroupAction.Replace -> "REPLACE"
                    | CorrectionGroupAction.Clear -> "CLEAR")

            if List.sort choices <> List.sort expected then
                invalidOp "Correction choices differ from the codec."

            let replacement =
                variants |> List.find (List.contains ("mode", Schema.Const "REPLACE"))

            let values = List.find (fst >> (=) "values") replacement |> snd

            {|
                name = name
                path = [ "groups"; name ]
                actions = choices
                fields = fields definitions [ "groups"; name; "values" ] values group.ReplaceFields
            |})

    let renderFrom definitions (commands: CommandDefinition list) =
        commands
        |> List.map (fun command ->
            let kind = CaseActKinds.token command.Kind
            let schema = Map.find ("CaseAct." + kind) definitions

            match command.Inputs with
            | CommandInputShape.Fields inputs ->
                JsonSerializer.SerializeToNode
                    {|
                        kind = kind
                        fields = fields definitions [] schema inputs
                    |}
            | CommandInputShape.CorrectionGroups inputs ->
                let groupSchema =
                    properties definitions schema |> List.find (fst >> (=) "groups") |> snd

                JsonSerializer.SerializeToNode
                    {|
                        kind = kind
                        groups = groups definitions groupSchema inputs
                    |})
        |> JsonSerializer.SerializeToUtf8Bytes

    let render () =
        renderFrom (OpenApi.definitions ()) CommandDefinitions.all
