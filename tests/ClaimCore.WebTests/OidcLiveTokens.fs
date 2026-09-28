module ClaimCore.WebTests.OidcLiveTokens

open System
open System.Text
open System.Text.Json

let private decode (source: string) =
    let normalized = source.Replace('-', '+').Replace('_', '/')
    let padded = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=')
    Convert.FromBase64String(padded)

let private encode (source: string) =
    source
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String
    |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

let alterHeader algorithm tokenType (token: string) =
    let parts = token.Split('.')

    if parts.Length <> 3 then
        invalidOp "Synthetic JWT has an invalid shape."

    use header = JsonDocument.Parse(decode parts[0])

    let kid =
        header.RootElement.GetProperty("kid").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Synthetic JWT key ID is missing.")

    let replacement =
        JsonSerializer.Serialize(
            {|
                alg = algorithm
                typ = tokenType
                kid = kid
            |}
        )
        |> encode

    String.Join('.', [| replacement; parts[1]; parts[2] |])
