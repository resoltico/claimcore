namespace ClaimCore.TestSupport

open System
open System.Diagnostics

/// CLI fixtures start without ambient product credentials or inherited coverage instrumentation.
module CliProcessEnvironment =
    let clearInherited (start: ProcessStartInfo) =
        start.Environment.Keys
        |> Seq.filter (fun name ->
            name.StartsWith("CLAIMCORE_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
        |> Seq.toArray
        |> Array.iter (fun name -> start.Environment.Remove(name) |> ignore)
