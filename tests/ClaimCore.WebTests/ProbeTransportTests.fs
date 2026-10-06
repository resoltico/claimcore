module ClaimCore.WebTests.ProbeTransportTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.ProbeHttpsFixture

let private retainsPublicIdentity () =
    withServer (fun root binding ->
        task {
            use transport = WebProbeTransport.create binding (Some root)
            use client = new HttpClient(transport)
            use! response = client.GetAsync(Uri(binding.Origin, "/health/live"))

            Expect.equal
                response.StatusCode
                HttpStatusCode.OK
                "The explicit socket connects through validated HTTPS"

            Expect.equal
                (response.Headers.GetValues("Observed-Host") |> Seq.exactlyOne)
                binding.Origin.Authority
                "HTTP identity retains the public authority"

            use! ready = client.GetAsync(Uri(binding.Origin, "/health/ready"))

            Expect.equal
                ready.StatusCode
                HttpStatusCode.ServiceUnavailable
                "Liveness cannot manufacture readiness"

            let loopback =
                { binding with
                    ListenAddress = IPAddress.Loopback
                }

            use explicitTransport = WebProbeTransport.create loopback (Some root)
            use explicitClient = new HttpClient(explicitTransport)
            use! direct = explicitClient.GetAsync(Uri(binding.Origin, "/health/live"))

            Expect.equal
                direct.StatusCode
                HttpStatusCode.OK
                "Explicit address also retains TLS identity"
        })

let private refusesCertificateMismatch () =
    withServer (fun root binding ->
        task {
            let foreign =
                { binding with
                    Origin = Uri("https://foreign.localhost:9443")
                }

            use transport = WebProbeTransport.create foreign (Some root)
            use client = new HttpClient(transport)

            let! failed =
                task {
                    try
                        use! _response = client.GetAsync(foreign.Origin)
                        return false
                    with :? HttpRequestException ->
                        return true
                }

            Expect.isTrue failed "A supplied root never permits a hostname mismatch"
            use systemTransport = WebProbeTransport.create binding None
            use systemClient = new HttpClient(systemTransport)

            let! untrusted =
                task {
                    try
                        use! _response = systemClient.GetAsync(binding.Origin)
                        return false
                    with :? HttpRequestException ->
                        return true
                }

            Expect.isTrue untrusted "System trust rejects an unadmitted synthetic root"
        })

let tests =
    testList
        "HTTPS probe transport"
        [
            testCaseAsync
                "[CC-WEB-001] probe retains TLS and HTTP public identity at a distinct socket"
                (async { do! retainsPublicIdentity () |> Async.AwaitTask })
            testCaseAsync
                "[CC-WEB-001] probe refuses mismatched names and untrusted roots"
                (async { do! refusesCertificateMismatch () |> Async.AwaitTask })
        ]
