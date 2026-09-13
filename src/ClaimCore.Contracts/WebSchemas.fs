namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
module WebSchemas =
    let responseFileName identifier =
        "web-v2.endpoint." + identifier + ".response.schema.json"

    let responseDefinitionName identifier = "Response_" + identifier

    let private sharedDefinitions (projection: ContractModel) =
        WebSchemaDefinitions.all projection.Semantic projection.DefinitionSchema.Root

    let responseDocument (projection: ContractModel) (endpoint: WebEndpoint) =
        {
            Identifier = "https://claimcore.local/contracts/" + responseFileName endpoint.Identifier
            Title = "ClaimCore Web v2 " + endpoint.Identifier + " response"
            Root = endpoint.Response
            Definitions = sharedDefinitions projection
        }

    let response projection endpoint =
        let document = responseDocument projection endpoint
        CanonicalJson.renderSchema document document.Root

    let responses (projection: ContractModel) =
        let endpointDefinitions =
            projection.WebEndpoints
            |> List.map (fun endpoint ->
                responseDefinitionName endpoint.Identifier, endpoint.Response)

        let root = endpointDefinitions |> List.map (fst >> Schema.reference) |> Schema.oneOf

        let document =
            {
                Identifier = "https://claimcore.local/contracts/web-v2.responses.schema.json"
                Title = "ClaimCore Web v2 endpoint responses"
                Root = root
                Definitions = sharedDefinitions projection @ endpointDefinitions
            }

        CanonicalJson.renderSchema document document.Root

    let hostFailure =
        let document =
            {
                Identifier = "https://claimcore.local/contracts/web-v2.host-failure.schema.json"
                Title = "ClaimCore Web v2 host failure"
                Root = WebSchemaDefinitions.hostFailure
                Definitions = []
            }

        CanonicalJson.renderSchema document document.Root
