namespace ClaimCore.Cli

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.Protocol
open ClaimCore.Json

module internal VerbOptions =
    let private distance (left: string) (right: string) =
        let costs = Array2D.zeroCreate<int> (left.Length + 1) (right.Length + 1)

        for i in 0 .. left.Length do
            costs[i, 0] <- i

        for j in 0 .. right.Length do
            costs[0, j] <- j

        for i in 1 .. left.Length do
            for j in 1 .. right.Length do
                costs[i, j] <-
                    min
                        (min (costs[i - 1, j] + 1) (costs[i, j - 1] + 1))
                        (costs[i - 1, j - 1] + (if left[i - 1] = right[j - 1] then 0 else 1))

        costs[left.Length, right.Length]

    let suggestions value choices =
        choices
        |> List.map (fun choice -> distance value choice, choice)
        |> List.sort
        |> List.filter (fun (cost, _) -> cost <= 3)
        |> List.truncate 3
        |> List.map snd

    let private assigned (argument: string) =
        let separator = argument.IndexOf '='

        if separator > 2 then
            Some(argument.Substring(0, separator), argument.Substring(separator + 1))
        else
            None

    let tokenize known switches repeated arguments (problems: ResizeArray<UsageError>) =
        let options = Dictionary<string, string option list>()
        let operands = ResizeArray<string>()

        let add name value =
            if options.ContainsKey name then
                if List.contains name repeated then
                    options[name] <- value :: options[name]
                else
                    problems.Add(UsageError.DuplicateOption name)
            else
                options.Add(name, [ value ])

        let rec scan =
            function
            | [] -> ()
            | (argument: string) :: rest when argument.StartsWith("--", StringComparison.Ordinal) ->
                let name, explicit =
                    match assigned argument with
                    | Some(name, value) -> name, Some value
                    | None -> argument, None

                if not (List.contains name known) then
                    problems.Add(
                        UsageError.UnknownOption(name, suggestions name known |> List.tryHead)
                    )

                match explicit with
                | Some value ->
                    if List.contains name switches then
                        problems.Add(UsageError.InvalidValue(name, value))
                    else
                        add name (Some value)

                    scan rest
                | None when List.contains name switches ->
                    add name (Some "true")
                    scan rest
                | None ->
                    match rest with
                    | value :: tail when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                        add name (Some value)
                        scan tail
                    | _ ->
                        if List.contains name known then
                            problems.Add(UsageError.MissingValue name)

                        add name None
                        scan rest
            | value :: rest ->
                operands.Add value
                scan rest

        scan arguments
        options, List.ofSeq operands

    let private booleanAtom name value (problems: ResizeArray<UsageError>) =
        match value with
        | "true" -> JsonValue.Boolean true
        | "false" -> JsonValue.Boolean false
        | _ ->
            problems.Add(UsageError.InvalidChoice(name, value, [ "true"; "false" ]))
            JsonValue.Null

    let private choice name choices value (problems: ResizeArray<UsageError>) =
        match
            choices
            |> List.tryFind (fun choice -> value = choice || value = CommandGrammar.kebab choice)
        with
        | Some choice -> JsonValue.String choice
        | None ->
            problems.Add(
                UsageError.InvalidChoice(name, value, List.map CommandGrammar.kebab choices)
            )

            JsonValue.Null

    let atom name kind value (problems: ResizeArray<UsageError>) =
        match kind with
        | CatalogAtom.Text -> JsonValue.String value
        | CatalogAtom.Boolean -> booleanAtom name value problems
        | CatalogAtom.Integer ->
            match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, number when number <= JsonValue.MaximumInteger -> JsonValue.Integer number
            | _ ->
                problems.Add(UsageError.InvalidValue(name, value))
                JsonValue.Null
        | CatalogAtom.Choice choices -> choice name choices value problems
        | CatalogAtom.Choices choices -> JsonValue.Array [ choice name choices value problems ]
        | CatalogAtom.Switch(off, on) -> JsonValue.String(if value = "true" then on else off)
