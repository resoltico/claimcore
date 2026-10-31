namespace ClaimCore.RecordFormat

open ClaimCore.Json

/// Closed permanent grammar for the retained final-nonacceptance control body.
module ControlRecord =
    let private codec =
        Codec.record (fun () () -> ())
        |> Codec.field "format" id (Codec.integer 2L 2L |> Codec.map (fun _ -> ()) (fun () -> 2L))
        |> Codec.field "kind" id (Codec.constant "NOT_ACCEPTED")
        |> Codec.build

    let encode () = Codec.encode codec ()

    let decode bytes =
        Codec.decode 128 codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
