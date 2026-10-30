module ClaimCore.TestSupport.PositiveExamples

open System.Text
open System.Text.Json
open ClaimCore.Json
open ClaimCore.Protocol

/// Supply behavior-test inputs only. Golden and permanent-byte assertions read their raw sources.
let value (sample: JsonElement) =
    let raw =
        sample.GetProperty("value").GetRawText()
        |> Encoding.UTF8.GetBytes
        |> StrictJson.read Limits.MaximumResponseBytes
        |> Result.defaultWith (fun problem -> invalidOp (string problem))

    if sample.GetProperty("valid").GetBoolean() then
        Inputs.positiveValue raw
    else
        raw

let forEndpoint filename endpoint =
    let path =
        System.IO.Path.Combine(RepositoryRoot.find (), "tests/fixtures", filename)

    use source = JsonDocument.Parse(System.IO.File.ReadAllBytes path)

    source.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.filter (fun sample ->
        sample.GetProperty("valid").GetBoolean()
        && sample.GetProperty("endpoint").GetString() = endpoint)
    |> Seq.map value
    |> Seq.toList
