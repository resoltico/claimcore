namespace ClaimCore.Cli

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json

/// The endpoint witness and decoded value are constructed together by its output codec.
[<CustomEquality; NoComparison>]
type TypedReply =
    private
        {
            Identity: EndpointId
            Value: objnull
            Write: unit -> JsonValue
        }

    member this.Endpoint = this.Identity

    override this.Equals other =
        match other with
        | :? TypedReply as value ->
            this.Identity = value.Identity && Unchecked.equals this.Value value.Value
        | _ -> false

    override this.GetHashCode() = hash (this.Identity, this.Value)

module internal TypedReply =
    let ofValue (endpoint: Endpoint<'Input, 'Output>) (value: 'Output) =
        {
            Identity = endpoint.Id
            Value = box value
            Write = fun () -> endpoint.Output.Write value
        }

    let read (endpoint: AnyEndpoint) value =
        endpoint.Accept
            { new IEndpointVisitor<Result<TypedReply, Problem>> with
                member _.Visit endpoint =
                    endpoint.Output.Read "$" value
                    |> Result.map (fun decoded ->
                        let typed = ofValue endpoint decoded
                        // Admission preserves the original wire representation for JSON delivery.
                        // Human rendering uses only the decoded value; neither path reparses it.
                        { typed with Write = fun () -> value })
            }

    let json reply = reply.Write()
    let value<'Value> reply : 'Value = unbox reply.Value
