namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.Collections.Generic
open ClaimCore.Presentation

module internal VerbOptions =
    let integer flag minimum maximum (value: string) =
        match
            System.Numerics.BigInteger.TryParse(
                value,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture
            )
        with
        | false, _ -> Error(UsageError.InvalidInteger(flag, value))
        | true, parsed when parsed < bigint (minimum: int64) || parsed > bigint (maximum: int64) ->
            Error(UsageError.InvalidRange(flag, value, minimum, maximum))
        | true, parsed -> Ok(int64 parsed)

    let uuid flag (value: string) =
        match System.Guid.TryParseExact(value, "D") with
        | true, id when id <> System.Guid.Empty -> Ok id
        | _ -> Error(UsageError.InvalidUuid flag)

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

    /// A terminator protects every following argv token from option interpretation.
    let beforeTerminator arguments = arguments |> List.takeWhile ((<>) "--")

    let insertOptions added arguments =
        let prefix = beforeTerminator arguments
        prefix @ added @ (arguments |> List.skip prefix.Length)

    let hasOption name arguments =
        beforeTerminator arguments
        |> List.exists (fun (value: string) ->
            value = name || value.StartsWith(name + "=", StringComparison.Ordinal))

    let private assigned (argument: string) =
        let separator = argument.IndexOf '='

        if separator > 2 then
            Some(argument.Substring(0, separator), argument.Substring(separator + 1))
        else
            None

    [<RequireQualifiedAccess>]
    type OptionValue =
        | Assigned of string
        | Supplied of string
        | Switch
        | Missing

    [<RequireQualifiedAccess>]
    type Argument =
        | Option of name: string * value: OptionValue
        | Operand of string

    /// Classify consumption once, before callers attach validation or help meaning.
    let classify switches arguments =
        let rec scan completed =
            function
            | [] -> List.rev completed
            | "--" :: rest -> List.rev completed @ (rest |> List.map Argument.Operand)
            | (argument: string) :: rest when argument.StartsWith("--", StringComparison.Ordinal) ->
                match assigned argument with
                | Some(name, value) ->
                    scan (Argument.Option(name, OptionValue.Assigned value) :: completed) rest
                | None when List.contains argument switches ->
                    scan (Argument.Option(argument, OptionValue.Switch) :: completed) rest
                | None ->
                    match rest with
                    | value :: tail when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                        scan
                            (Argument.Option(argument, OptionValue.Supplied value) :: completed)
                            tail
                    | _ -> scan (Argument.Option(argument, OptionValue.Missing) :: completed) rest
            | value :: rest -> scan (Argument.Operand value :: completed) rest

        scan [] arguments

    let tokenize known available switches repeated arguments (problems: ResizeArray<UsageError>) =
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

        for argument in classify switches arguments do
            match argument with
            | Argument.Operand value -> operands.Add value
            | Argument.Option(name, value) ->
                if not (List.contains name known) then
                    problems.Add(
                        UsageError.UnknownOption(name, suggestions name available |> List.tryHead)
                    )

                match value with
                | OptionValue.Assigned value when List.contains name switches ->
                    problems.Add(UsageError.InvalidValue(name, value))
                | OptionValue.Assigned value
                | OptionValue.Supplied value -> add name (Some value)
                | OptionValue.Switch -> add name (Some "true")
                | OptionValue.Missing ->
                    if List.contains name known then
                        problems.Add(UsageError.MissingValue name)

                    add name None

        options, List.ofSeq operands
