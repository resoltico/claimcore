namespace ClaimCore.Contracts

open System
open System.Text.Json
open SchemaFailures

/// One bounded traversal owns both detailed intake refusal and Boolean response verification.
module SchemaValueValidation =
    let private properties path (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail path SchemaProblem.ObjectRequired
        else
            let values = element.EnumerateObject() |> Seq.toList
            let names = values |> List.map _.Name

            if names.Length <> (names |> Set.ofList |> Set.count) then
                fail path SchemaProblem.DuplicateMember
            else
                Ok(values |> List.map (fun item -> item.Name, item.Value) |> Map.ofList)

    let private every check values =
        values
        |> List.tryPick (fun value ->
            match check value with
            | Ok() -> None
            | Error reason -> Some reason)
        |> Option.map Error
        |> Option.defaultValue (Ok())

    let rec private constants definitions depth =
        function
        | ReferenceSchema name when depth < 64 ->
            Map.tryFind name definitions
            |> Option.map (constants definitions (depth + 1))
            |> Option.defaultValue Map.empty
        | ObjectSchema value ->
            value.Properties
            |> List.choose (fun property ->
                match property.Required, property.Schema with
                | true, ConstantSchema constant -> Some(property.Name, constant)
                | _ -> None)
            |> Map.ofList
        | _ -> Map.empty

    let private selectedAlternative definitions path alternatives (element: JsonElement) =
        let selectors = alternatives |> List.map (constants definitions 0)

        let common =
            selectors |> List.map (Map.keys >> Set.ofSeq) |> List.reduce Set.intersect

        let discriminator =
            common
            |> Set.toList
            |> List.tryFind (fun name ->
                selectors |> List.map (Map.find name) |> Set.ofList |> Set.count > 1)

        match discriminator, properties path element with
        | Some name, Ok values ->
            match Map.tryFind name values with
            | None -> Some(fail (path @ [ name ]) SchemaProblem.MissingMember)
            | Some value ->
                let matching =
                    List.zip alternatives selectors
                    |> List.filter (fun (_, selector) ->
                        SchemaPrimitiveValidation.constantMatches (Map.find name selector) value)

                match matching with
                | [ alternative, _ ] -> Some(Ok alternative)
                | [] -> Some(fail (path @ [ name ]) SchemaProblem.InvalidValue)
                | _ -> None
        | Some _, Error failure -> Some(Error failure)
        | None, _ -> None

    let rec private check definitions depth path schema element =
        if depth > 64 then
            fail path SchemaProblem.InvalidValue
        else
            match SchemaPrimitiveValidation.check path schema element with
            | Some answer -> answer
            | None -> composite definitions depth path schema element

    and private composite definitions depth path schema (element: JsonElement) =
        let child schema value =
            check definitions (depth + 1) path schema value

        match schema with
        | ObjectSchema constraints -> objectValue definitions depth path constraints element
        | ArraySchema constraints ->
            if element.ValueKind <> JsonValueKind.Array then
                fail path SchemaProblem.ArrayRequired
            else
                let values = element.EnumerateArray() |> Seq.toList

                if
                    (constraints.MinimumItems
                     |> Option.exists (fun minimum -> values.Length < minimum))
                    || (constraints.MaximumItems
                        |> Option.exists (fun maximum -> values.Length > maximum))
                then
                    fail path SchemaProblem.IntegerRange
                else
                    every (child constraints.Item) values
        | TupleSchema schemas ->
            if element.ValueKind <> JsonValueKind.Array then
                fail path SchemaProblem.ArrayRequired
            else
                let values = element.EnumerateArray() |> Seq.toList

                if values.Length <> schemas.Length then
                    fail path SchemaProblem.IntegerRange
                else
                    List.zip schemas values |> every (fun (schema, value) -> child schema value)
        | DictionarySchema value ->
            properties path element
            |> Result.bind (Map.values >> Seq.toList >> every (child value))
        | OneOfSchema alternatives -> unionValue definitions depth path alternatives element
        | ReferenceSchema name ->
            match Map.tryFind name definitions with
            | Some target -> child target element
            | None -> fail path SchemaProblem.InvalidValue
        | _ -> fail path SchemaProblem.InvalidValue

    and private objectValue definitions depth path constraints element =
        properties path element
        |> Result.bind (fun supplied ->
            let declared = constraints.Properties |> List.map _.Name |> Set.ofList

            if
                not constraints.AdditionalProperties
                && supplied |> Map.exists (fun name _ -> not (Set.contains name declared))
            then
                fail path SchemaProblem.UnknownMember
            else
                constraints.Properties
                |> every (fun property ->
                    match Map.tryFind property.Name supplied with
                    | None when property.Required ->
                        fail (path @ [ property.Name ]) SchemaProblem.MissingMember
                    | None -> Ok()
                    | Some value ->
                        check
                            definitions
                            (depth + 1)
                            (path @ [ property.Name ])
                            property.Schema
                            value))

    and private unionValue definitions depth path alternatives element =
        let results =
            alternatives
            |> List.map (fun schema -> check definitions (depth + 1) path schema element)

        match results |> List.filter Result.isOk |> List.length with
        | 1 -> Ok()
        | 0 when not alternatives.IsEmpty ->
            match selectedAlternative definitions path alternatives element with
            | Some(Ok selected) -> check definitions (depth + 1) path selected element
            | Some(Error failure) -> Error failure
            | None -> fail path SchemaProblem.InvalidValue
        | _ -> fail path SchemaProblem.InvalidValue

    let admit (document: SchemaDocument) (element: JsonElement) =
        try
            check (document.Definitions |> Map.ofList) 0 [] document.Root element
        with
        | :? InvalidOperationException
        | :? ArgumentException -> fail [] SchemaProblem.InvalidValue

    let verify document element = admit document element |> Result.isOk
