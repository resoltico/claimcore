module ClaimCore.TestSupport.DecodingExamples

open System.Text
open System.Text.Json
open ClaimCore.Json
open ClaimCore.Wire

/// Preserve every supplied member and scalar in literal conformance inputs.
let value (sample: JsonElement) =
    sample.GetProperty("value").GetRawText()
    |> Encoding.UTF8.GetBytes
    |> StrictJson.read Limits.MaximumResponseBytes
    |> Result.defaultWith (fun problem -> invalidOp (string problem))
