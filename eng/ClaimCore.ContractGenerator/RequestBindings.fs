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
        | Schema.Object(fields, []) ->
            let names = fields |> List.map fst

            if (List.distinct names).Length <> names.Length then
                invalidOp "Duplicate request member bindings."

            fields
        | _ -> invalidOp "Request bindings require a closed required object."

    let rec private leaves definitions prefix schema =
        match resolve definitions schema with
        | Schema.Const _ -> invalidOp "Unsupported literal in request input bindings."
        | Schema.String _ -> [ List.last prefix, prefix ]
        | Schema.Object([], []) when not (List.isEmpty prefix) ->
            invalidOp "Unsupported required empty input object."
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

    let private choice definitions (group: CorrectionGroupDefinition) prefix variant =
        let members = properties definitions variant

        let mode =
            match List.tryFind (fst >> (=) "mode") members with
            | Some(_, Schema.Const mode) -> mode
            | _ -> invalidOp "Correction choice has no constant mode."

        let expected =
            if mode = "REPLACE" then
                [ "mode"; "values" ]
            else
                [ "mode" ]

        if (members |> List.map fst |> List.sort) <> List.sort expected then
            invalidOp "Unsupported required correction choice members."

        let bindings =
            if mode = "REPLACE" then
                fields
                    definitions
                    (prefix @ [ "values" ])
                    (List.find (fst >> (=) "values") members |> snd)
                    group.ReplaceFields
            else
                []

        mode, bindings

    let private group definitions actual (group: CorrectionGroupDefinition) =
        let name = CorrectionGroup.token group.Name

        let variants =
            match resolve definitions (List.find (fst >> (=) name) actual |> snd) with
            | Schema.OneOf(variants, _) -> variants
            | _ -> invalidOp "Correction binding requires tagged choices."

        let bindings = variants |> List.map (choice definitions group [ "groups"; name ])
        let choices = bindings |> List.map fst
        let expected = group.Actions |> List.map CorrectionActions.token

        if
            List.sort choices <> List.sort expected
            || (List.distinct choices).Length <> choices.Length
        then
            invalidOp "Correction choices differ from the codec."

        {|
            name = name
            path = [ "groups"; name ]
            actions = choices
            fields = List.find (fst >> (=) "REPLACE") bindings |> snd
        |}

    let private groups definitions schema definitionsOfGroups =
        let actual = properties definitions schema

        if
            (actual |> List.map fst |> List.sort)
            <> (definitionsOfGroups
                |> List.map (fun group -> CorrectionGroup.token group.Name)
                |> List.sort)
        then
            invalidOp "Correction group bindings are incomplete."

        definitionsOfGroups |> List.map (group definitions actual)

    let private content definitions kind schema =
        let members = properties definitions schema

        match List.tryFind (fst >> (=) "kind") members with
        | Some(_, Schema.Const actual) when actual = kind -> ()
        | _ -> invalidOp "Command discriminator differs from the semantic act."

        members |> List.filter (fst >> (<>) "kind")

    let private commandCoverage definitions (commands: CommandDefinition list) =
        let variants =
            match resolve definitions (Map.find "CaseAct" definitions) with
            | Schema.OneOf(variants, _) -> variants
            | _ -> invalidOp "Case act binding requires a tagged union."

        let actual =
            variants
            |> List.map (fun variant ->
                match properties definitions variant |> List.tryFind (fst >> (=) "kind") with
                | Some(_, Schema.Const kind) -> kind
                | _ -> invalidOp "Act variant has no constant discriminator.")

        let expected = commands |> List.map (fun command -> CaseActKinds.token command.Kind)

        if
            List.sort actual <> List.sort expected
            || (List.distinct expected).Length <> expected.Length
        then
            invalidOp "Core commands and codec act variants differ."

    let renderFrom definitions (commands: CommandDefinition list) =
        commandCoverage definitions commands

        commands
        |> List.map (fun command ->
            let kind = CaseActKinds.token command.Kind
            let schema = Map.find ("CaseAct." + kind) definitions
            let members = content definitions kind schema

            match command.Inputs with
            | CommandInputShape.Fields inputs ->
                JsonSerializer.SerializeToNode
                    {|
                        kind = kind
                        fields = fields definitions [] (Schema.Object(members, [])) inputs
                    |}
            | CommandInputShape.CorrectionGroups inputs ->
                if List.map fst members <> [ "groups" ] then
                    invalidOp "Unsupported grouped command members."

                let groupSchema = members |> List.exactlyOne |> snd

                JsonSerializer.SerializeToNode
                    {|
                        kind = kind
                        groups = groups definitions groupSchema inputs
                    |})
        |> JsonSerializer.SerializeToUtf8Bytes

    let render () =
        renderFrom (OpenApi.definitions ()) CommandDefinitions.all
