namespace ClaimCore.Application

open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Only the application can prepare the request admitted to a persistence transaction.
type internal PreparedOperation =
    private
        {
            RequestValue: CommandRequest
            FingerprintValue: string
            CanonicalRequestValue: byte array
        }

module internal Operation =
    let prepare request =
        Claim.validateRequest request
        |> Result.map (fun () ->
            let canonicalRequest = RequestRecord.encode request

            {
                RequestValue = request
                CanonicalRequestValue = canonicalRequest
                FingerprintValue = canonicalRequest |> SHA256.HashData |> Convert.ToHexStringLower
            })

    let request operation = operation.RequestValue
    let fingerprint operation = operation.FingerprintValue
    let canonicalRequest operation = operation.CanonicalRequestValue
