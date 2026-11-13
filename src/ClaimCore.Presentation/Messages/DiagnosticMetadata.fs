namespace ClaimCore.Presentation


[<RequireQualifiedAccess>]
type DiagnosticParameterRule =
    | IntegerRange of minimum: int * maximum: int
    | Vocabulary of name: string * values: string list

type DiagnosticParameterDefinition =
    {
        Name: string
        Rule: DiagnosticParameterRule
    }

type DiagnosticDefinition =
    {
        Id: string
        Parameters: DiagnosticParameterDefinition list
    }
