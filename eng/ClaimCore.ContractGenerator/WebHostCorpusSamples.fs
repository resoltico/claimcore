namespace ClaimCore.ContractGeneration

open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type internal WebHostSample =
    {
        Identifier: string
        Status: int
        Code: string
        Phase: string option
        Bytes: byte array
    }

[<RequireQualifiedAccess>]
module internal WebHostCorpusSamples =
    let private sample reason =
        {
            Identifier = WebHostFailures.token reason
            Status = WebHostFailures.status reason
            Code = WebHostFailures.code reason
            Phase = WebHostFailures.phase reason
            Bytes = WebWireCodec.hostFailure reason
        }

    let all = WebHostFailures.all |> List.map sample

    let assertComplete () =
        let statuses = all |> List.map _.Status |> Set.ofList
        let expectedStatuses = WebSchemaDefinitions.hostFailureStatuses |> Set.ofList

        if statuses <> expectedStatuses then
            invalidOp "Web host corpus must cover every generated allowed status."

        let phases = all |> List.map _.Phase |> Set.ofList

        if phases <> Set.ofList [ None; Some "NOT_STARTED"; Some "STARTED_UNCONFIRMED" ] then
            invalidOp "Web host corpus must cover every execution-phase branch."
