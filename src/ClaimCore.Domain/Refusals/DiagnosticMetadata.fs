namespace ClaimCore.Domain

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
    let vocabulary =
        Vocabulary.ofUnion<DiagnosticArgument> "argument" TokenCasing.CamelCase None

    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

    let definition argument =
        {
            Name = token argument
            Minimum =
                match argument with
                | DiagnosticArgument.MaximumIntegerDigits
                | DiagnosticArgument.MaximumPageSize -> 1
                | DiagnosticArgument.MaximumCharacters
                | DiagnosticArgument.MaximumFractionalDigits -> 0
            Maximum = System.Int32.MaxValue
        }
