module ClaimCore.WebTests.OidcLiveTokens

open System
open System.Text
open System.Text.Json
open Expecto

let private decode (source: string) =
    let normalized = source.Replace('-', '+').Replace('_', '/')
    let padded = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=')
    Convert.FromBase64String(padded)

let private encode (source: string) =
    source
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String
    |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

// Only schedules the final real-host expiry assertion after this fixture token was accepted.
let expiryDelay now (token: string) =
    let parts = token.Split('.')

    if parts.Length <> 3 then
        invalidOp "Synthetic JWT has an invalid shape."

    use payload = JsonDocument.Parse(decode parts[1])

    let expires =
        DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("exp").GetInt64())

    max TimeSpan.Zero (expires.AddSeconds(1.) - now)

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

[<Tests>]
let tests =
    testCase "owned token expiry wait subtracts elapsed qualification time" (fun () ->
        let token = "e30." + encode "{\"exp\":100}" + ".synthetic-signature"

        for now, seconds in [ 90L, 11.; 99L, 2.; 100L, 1.; 101L, 0.; 120L, 0. ] do
            Expect.equal
                (expiryDelay (DateTimeOffset.FromUnixTimeSeconds now) token)
                (TimeSpan.FromSeconds seconds)
                "Wait reaches real expiration with one second margin, never a negative delay"

        for malformed in [ "missing"; "e30.e30.synthetic-signature" ] do
            Expect.throws
                (fun () -> expiryDelay DateTimeOffset.UnixEpoch malformed |> ignore)
                "Missing fixture expiration cannot silently skip the real expiry boundary")
