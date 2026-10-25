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

    let vocabulary =
        definitions
        |> List.map (fun (value, token, _) -> value, token)
        |> Vocabulary.define "argument"

    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

    let definition argument =
        let _, name, minimum =
            definitions |> List.find (fun (candidate, _, _) -> candidate = argument)

        {
            Name = name
            Minimum = minimum
            Maximum = System.Int32.MaxValue
        }
