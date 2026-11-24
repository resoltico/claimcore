namespace ClaimCore.Server

open System
open System.IO
open System.Text.Json
open System.Threading
open ClaimCore.Application

module internal StorageFailureLog =
    let private request = AsyncLocal<(CancellationToken * CancellationToken) option>()

    let clientDisconnected () =
        request.Value
        |> Option.exists (fun (client, stopping) ->
            client.IsCancellationRequested && not stopping.IsCancellationRequested)

    let withinRequest client stopping =
        let previous = request.Value
        request.Value <- Some(client, stopping)

        { new IDisposable with
            member _.Dispose() = request.Value <- previous
        }

    let attach (writer: TextWriter) =
        ClaimCore.Application.Runtime.subscribeFailures (fun value ->
            if value.Classification <> FailureClass.Cancelled || not (clientDisconnected ()) then
                // Closed union tokens and internally generated GUIDs only. Never format exceptions.
                let line =
                    JsonSerializer.Serialize
                        {|
                            kind = "storageFailure"
                            attempt = value.Correlation
                            correlation = value.RequestCorrelation |> Option.toNullable
                            phase = string value.Phase
                            classification = string value.Classification
                        |}

                try
                    writer.WriteLine line
                with _ ->
                    ())
