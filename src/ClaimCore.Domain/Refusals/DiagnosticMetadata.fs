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
        Vocabulary.define
            "argument"
            [
                DiagnosticArgument.MaximumCharacters
                DiagnosticArgument.MaximumIntegerDigits
                DiagnosticArgument.MaximumFractionalDigits
                DiagnosticArgument.MaximumPageSize
            ]
            (function
             | DiagnosticArgument.MaximumCharacters -> "maximumCharacters"
             | DiagnosticArgument.MaximumIntegerDigits -> "maximumIntegerDigits"
             | DiagnosticArgument.MaximumFractionalDigits -> "maximumFractionalDigits"
             | DiagnosticArgument.MaximumPageSize -> "maximumPageSize")

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
