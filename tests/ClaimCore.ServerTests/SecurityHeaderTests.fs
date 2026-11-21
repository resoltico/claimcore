module ClaimCore.ServerTests.SecurityHeaderTests

open System.Net
open System.Net.Http
open System.Text
open ClaimCore.TestSupport.TestServerFixture
open ClaimCore.Wire
open Expecto

let private permissions =
    "accelerometer=(), ambient-light-sensor=(), autoplay=(), bluetooth=(), browsing-topics=(), camera=(), "
    + "clipboard-read=(), display-capture=(), encrypted-media=(), geolocation=(), gyroscope=(), hid=(), "
    + "idle-detection=(), local-fonts=(), magnetometer=(), microphone=(), midi=(), payment=(), "
    + "publickey-credentials-create=(), publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), xr-spatial-tracking=()"

let assertPolicies label (response: HttpResponseMessage) =
    let header name =
        match response.Headers.TryGetValues name with
        | true, values -> values |> nonNull |> Seq.toList
        | false, _ -> []

    Expect.equal (header "Strict-Transport-Security") [ "max-age=31536000" ] ($"HSTS on {label}")

    Expect.equal (header "Permissions-Policy") [ permissions ] ($"Permissions Policy on {label}")

[<Tests>]
let tests =
    testCaseAsync
        "security headers cover public, private, viewer and failure responses"
        (async {
            let asset = "assets/public-12345678.js"
            let bytes = Encoding.UTF8.GetBytes "export const synthetic = true;"
            use host = new Host(true, buildAssets = [ asset, bytes ])

            for path, expected in
                [
                    "/api/session", HttpStatusCode.OK
                    "/api/missing", HttpStatusCode.NotFound
                    "/" + asset, HttpStatusCode.OK
                    "/attachment-preview/viewer", HttpStatusCode.OK
                ] do
                use! response =
                    host.SendResponse(
                        HttpMethod.Get,
                        path,
                        None,
                        [ "ClaimCore-Contract", ContractIdentity.value ]
                    )
                    |> Async.AwaitTask

                Expect.equal response.StatusCode expected path
                assertPolicies path response
        })
