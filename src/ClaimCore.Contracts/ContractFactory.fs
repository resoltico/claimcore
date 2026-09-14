namespace ClaimCore.Contracts

open ClaimCore.Application

module ContractProjection =
    /// This boundary accepts only Application's static semantic model, never legacy response values.
    let create (semantic: SemanticCoreContract) =
        let cliEndpoints = CliEndpointCatalog.all semantic
        let definitionSchema = ProjectionSchema.definitionDocument semantic

        {
            Semantic = semantic
            DefinitionSchema = definitionSchema
            CliEndpoints = cliEndpoints
            CliResponses = CliResponseSchemas.all semantic |> Map.ofList
            WebEndpoints = WebEndpointCatalog.all semantic
        }

    let current () =
        ClaimCore.Application.SemanticContract.current |> create
