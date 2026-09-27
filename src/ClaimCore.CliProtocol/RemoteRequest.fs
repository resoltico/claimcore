namespace ClaimCore.Cli

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
type RemoteBody =
    | Json of byte array
    | Raw of bytes: byte array * mediaType: string * sourceDigest: string option

type RemoteRequest =
    {
        Contract: WebEndpoint
        Body: RemoteBody
        Destination: string option
        OperationId: Guid option
    }

module RemoteRequest =
    let private text (source: JsonElement) (name: string) =
        source.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""

    let private webEndpoint identifier =
        (ContractProjection.current ()).WebEndpoints
        |> List.tryFind (fun value -> value.Identifier = identifier)

    let private rawRequest contract mediaType maximum path digest =
        PrivateFiles.readSource maximum path
        |> Result.mapError (fun _ -> "PRIVATE_SOURCE_UNAVAILABLE")
        |> Result.bind (fun bytes ->
            let actual = SHA256.HashData(bytes) |> Convert.ToHexStringLower

            if digest |> Option.exists ((<>) actual) then
                Error "PRIVATE_SOURCE_CHANGED"
            else
                Ok
                    {
                        Contract = contract
                        Body = RemoteBody.Raw(bytes, mediaType, digest)
                        Destination = None
                        OperationId = None
                    })

    let create identifier (source: JsonElement) =
        match webEndpoint identifier with
        | None -> Error "SERVICE_ENDPOINT_UNAVAILABLE"
        | Some contract ->
            match identifier, contract.Body with
            | "recovery.export", Some(JsonBody _) ->
                let operationId = source.GetProperty("operationId").GetGuid()
                let digest = text source "requestSha256"
                let destination = text source "destination"

                Ok
                    {
                        Contract = contract
                        Body = RemoteBody.Json(CliServiceRequests.export operationId digest)
                        Destination = Some destination
                        OperationId = Some operationId
                    }
            | "recovery.importEnvelopePreview", Some(RawBody(mediaType, maximum, _)) ->
                rawRequest contract mediaType maximum (text source "source") None
            | "recovery.importEnvelopeRetain", Some(RawBody(mediaType, maximum, _)) ->
                rawRequest
                    contract
                    mediaType
                    maximum
                    (text source "source")
                    (Some(text source "sourceSha256"))
            | _, Some(JsonBody _) ->
                let bytes = Encoding.UTF8.GetBytes(source.GetRawText())

                Ok
                    {
                        Contract = contract
                        Body = RemoteBody.Json bytes
                        Destination = None
                        OperationId = None
                    }
            | _ -> Error "SERVICE_ENDPOINT_MISMATCH"
