namespace ClaimCore.Web

open System
open System.IO
open ClaimCore.Contracts

/// HTTP allocation and transport failure handling; pure payload decoding belongs to Contracts.
module HttpBody =
    let readBounded limit (stream: Stream) =
        task {
            if limit < 1 then
                invalidArg (nameof limit) "The request byte limit must be positive."

            try
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 4096
                let mutable total = 0
                let mutable complete = false

                while total <= limit && not complete do
                    let! count = stream.ReadAsync(buffer, 0, buffer.Length)

                    if count = 0 then
                        complete <- true
                    elif count > limit - total then
                        total <- limit + 1
                    else
                        total <- total + count
                        output.Write(buffer, 0, count)

                if total > limit then
                    return Error HttpInputProblem.BodyTooLarge
                else
                    return Ok(output.ToArray())
            with
            | :? Microsoft.AspNetCore.Http.BadHttpRequestException as failure when
                failure.StatusCode = 413
                ->
                return Error HttpInputProblem.BodyTooLarge
            | :? OperationCanceledException -> return Error HttpInputProblem.BodyCancelled
            | :? IOException -> return Error HttpInputProblem.BodyUnreadable
        }
