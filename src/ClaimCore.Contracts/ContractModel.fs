namespace ClaimCore.Contracts

open ClaimCore.Application

type CliEndpoint =
    {
        Identifier: string
        Input: Schema
        Cancellable: bool
    }

type WebRequestBody =
    | JsonBody of Schema
    | RawBody of mediaType: string * maximumBytes: int * requiredHeaders: (string * Schema) list

type WebEndpoint =
    {
        Identifier: string
        Method: string
        Path: string
        Body: WebRequestBody option
        Response: Schema
        SuccessMediaType: string option
    }

/// Static projection shared by the semantic, CLI, and Web canonical contracts.
type ContractModel =
    {
        Semantic: SemanticCoreContract
        DefinitionSchema: SchemaDocument
        CliEndpoints: CliEndpoint list
        CliResponses: Map<string, Schema>
        WebEndpoints: WebEndpoint list
    }
