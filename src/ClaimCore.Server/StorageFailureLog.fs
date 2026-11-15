namespace ClaimCore.Server

open System
open System.IO
open System.Text.Json
open ClaimCore.Postgres

module internal StorageFailureLog =
    let attach (writer: TextWriter) =
        StorageDiagnostics.subscribe (fun value ->
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
