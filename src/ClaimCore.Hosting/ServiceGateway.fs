namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.ClientProtocol

/// Verified identity facts supplied by the HTTP authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedSubject = private AuthenticatedSubject of PrincipalKey

module AuthenticatedSubjects =
    let human issuer subject =
        PrincipalKey.human issuer subject |> Result.map AuthenticatedSubject

    let service issuer subject =
        PrincipalKey.service issuer subject |> Result.map AuthenticatedSubject

/// The host can route and limit requests without access to typed core inputs.
[<NoEquality; NoComparison>]
type ServiceEndpoint =
    {
        Identifier: string
        Method: string
        Path: string
        Mutates: bool
    }

[<NoEquality; NoComparison>]
type ServiceRequest = private ServiceRequest of string * ServiceInput

[<NoEquality; NoComparison>]
type ServiceReply =
    {
        Bytes: byte array
        Outcome: string
        OperationId: Guid option
        ActorId: Guid option
    }

type IProtocolService =
    abstract Invoke: AuthenticatedSubject * ServiceRequest * CancellationToken -> Task<ServiceReply>

module ServiceGateway =
    let requestByteLimit = ServiceLimits.RequestByteLimit

    let endpoints =
        EndpointCatalog.all
        |> List.map (fun value ->
            {
                Identifier = value.Identifier
                Method = value.Method
                Path = value.Path
                Mutates = value.Mutates
            })

    let matches = EndpointCatalog.matches

    let admit endpoint bytes =
        EndpointCatalog.admitRequest endpoint bytes
        |> Result.map (fun input -> ServiceRequest(endpoint, input))

    let session authenticated antiforgery =
        OutcomeCodec.session authenticated antiforgery |> OutcomeCodec.encode "session"

    let logout () =
        OutcomeCodec.logout () |> OutcomeCodec.encode "session.logout"

    let private operationId =
        function
        | ServiceInput.Case value -> Some value.OperationId
        | ServiceInput.Authority value -> Some value.OperationId
        | ServiceInput.Operation value -> Some value
        | _ -> None

    let requestOperationId (ServiceRequest(_, input)) = operationId input

    let private actorId =
        function
        | ExecuteOutcome.Accepted receipt -> Some receipt.RecordedBy
        | _ -> None

    let private dispatch (core: IActorClaimsCore) endpoint input token =
        task {
            match endpoint, input with
            | "context", ServiceInput.Empty ->
                let! value = core.Context token in return OutcomeCodec.context value, None
            | "case.get", ServiceInput.CaseReference reference ->
                let! value = core.Get(reference, token) in
                return OutcomeCodec.currentCase value, None
            | "case.list", ServiceInput.CaseList request ->
                let! value = core.List(request, token) in return OutcomeCodec.caseList value, None
            | "case.history", ServiceInput.History request ->
                let! value = core.History(request, token) in return OutcomeCodec.history value, None
            | "operation.observe", ServiceInput.Operation id ->
                let! value = core.Observe(id, token) in return OutcomeCodec.operation value, None
            | "authority.list", ServiceInput.ActorList request ->
                let! value = core.ListActors(request, token) in
                return OutcomeCodec.actors value, None
            | "case.preview", ServiceInput.Case request ->
                let! value = core.Preview(request, token) in return OutcomeCodec.preview value, None
            | "case.execute", ServiceInput.Case request ->
                let! value = core.Execute(request, token) in
                return OutcomeCodec.execute value, actorId value
            | "authority.execute", ServiceInput.Authority request ->
                let! value = core.ExecuteAuthority(request, token) in
                return OutcomeCodec.execute value, actorId value
            | _ -> return invalidArg "input" "The admitted input does not match its endpoint."
        }

    let internal bind (forActor: PrincipalKey -> IActorClaimsCore) =
        { new IProtocolService with
            member _.Invoke
                (AuthenticatedSubject principal, ServiceRequest(endpoint, input), cancellation)
                =
                task {
                    let definition = EndpointCatalog.tryFind endpoint |> Option.get

                    let token =
                        if definition.Mutates then
                            CancellationToken.None
                        else
                            cancellation

                    let! outcome, actor = dispatch (forActor principal) endpoint input token
                    let id = operationId input

                    let bytes =
                        match input, id with
                        | (ServiceInput.Case _ | ServiceInput.Authority _), Some value ->
                            OutcomeCodec.encodeOperation endpoint value outcome
                        | _ -> OutcomeCodec.encode endpoint outcome

                    return
                        {
                            Bytes = bytes
                            Outcome = (outcome["tag"] |> nonNull).GetValue<string>()
                            OperationId = id
                            ActorId = actor
                        }
                }
        }
