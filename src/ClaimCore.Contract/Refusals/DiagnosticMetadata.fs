namespace ClaimCore.Contract

type DiagnosticParameterDefinition =
    {
        Name: string
        Minimum: int
        Maximum: int
    }

type DiagnosticDefinition =
    {
        Id: string
        Parameters: DiagnosticParameterDefinition list
    }

[<RequireQualifiedAccess>]
type DiagnosticArgument =
    | MaximumCharacters
    | MaximumIntegerDigits
    | MaximumFractionalDigits
    | MaximumPageSize

module DiagnosticArguments =
    let private definitions =
        [
            DiagnosticArgument.MaximumCharacters, "maximumCharacters", 0
            DiagnosticArgument.MaximumIntegerDigits, "maximumIntegerDigits", 1
            DiagnosticArgument.MaximumFractionalDigits, "maximumFractionalDigits", 0
            DiagnosticArgument.MaximumPageSize, "maximumPageSize", 1
        ]

    let token argument =
        definitions
        |> List.find (fun (candidate, _, _) -> candidate = argument)
        |> fun (_, name, _) -> name

    let tryParse name =
        definitions
        |> List.tryFind (fun (_, candidate, _) -> candidate = name)
        |> Option.map (fun (argument, _, _) -> argument)

    let definition argument =
        let _, name, minimum =
            definitions |> List.find (fun (candidate, _, _) -> candidate = argument)

        {
            Name = name
            Minimum = minimum
            Maximum = System.Int32.MaxValue
        }
