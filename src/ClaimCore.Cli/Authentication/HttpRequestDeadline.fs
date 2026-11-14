namespace ClaimCore.Cli

open System.Net.Http
open System.Threading

/// ResponseHeadersRead does not carry HttpClient's timeout into content reads.
module internal HttpRequestDeadline =
    let link (client: HttpClient) (cancelled: CancellationToken) =
        let deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled)

        if client.Timeout <> Timeout.InfiniteTimeSpan then
            deadline.CancelAfter(client.Timeout)

        deadline
