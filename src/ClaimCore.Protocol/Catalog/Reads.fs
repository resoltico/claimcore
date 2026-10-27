namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ReadInput =
    | CaseOperand
    | OperationOperand
    | ActorOperand
    | Page
    | Detail

type ReadDeclaration =
    {
        Endpoint: QueryEndpoint
        Inputs: ReadInput list
    }


module ReadCatalog =
    let declarations =
        [
            {
                Endpoint = QueryEndpoint.CaseGet
                Inputs = [ ReadInput.CaseOperand ]
            }
            {
                Endpoint = QueryEndpoint.CaseList
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.CaseHistory
                Inputs = [ ReadInput.CaseOperand; ReadInput.Detail; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.Observe
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryEndpoint.Resolve
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityList
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityGrants
                Inputs = [ ReadInput.ActorOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.Context
                Inputs = []
            }
        ]
