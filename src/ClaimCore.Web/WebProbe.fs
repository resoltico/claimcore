namespace ClaimCore.Web

open System
open System.Net
open System.Net.Http
open System.Threading
open ClaimCore.Contracts

/// A probe has no runtime factory, database credential reader or schema/authority mutation.
module internal WebProbe =
    let private value name fallback =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue fallback

    let private binding () =
        let origin =
            value "CLAIMCORE_WEB_ORIGIN" "https://localhost:5443" |> WebBindings.parseOrigin

        let port = value "CLAIMCORE_WEB_LISTEN_PORT" (string origin.Port)

        let parsed =
            match Int32.TryParse(port) with
            | true, number -> number
            | _ ->
                WebStartupDiagnostics.refuse (
                    WebStartupProblem.InvalidSetting WebSetting.ListenPort
                )

        WebBindings.create origin (value "CLAIMCORE_WEB_LISTEN_ADDRESS" "127.0.0.1") parsed

    let run (path: string) =
        task {
            let target = binding ()
            let rootPath = value "CLAIMCORE_WEB_PROBE_CA_CERT_FILE" ""

            let trusted =
                if rootPath = "" then
                    None
                elif HttpsOrigins.isLocal target.Origin then
                    Some(OidcTrustRoot.load rootPath)
                else
                    WebStartupDiagnostics.refuse (
                        WebStartupProblem.InvalidSetting WebSetting.ProbeCaCertFile
                    )

            try
                try
                    use transport = WebProbeTransport.create target trusted
                    use client = new HttpClient(transport, Timeout = TimeSpan.FromSeconds(5.))

                    use! response =
                        client.GetAsync(
                            Uri(target.Origin, path),
                            HttpCompletionOption.ResponseHeadersRead
                        )

                    return if response.StatusCode = HttpStatusCode.OK then 0 else 3
                with
                | :? HttpRequestException
                | :? OperationCanceledException -> return 3
            finally
                trusted |> Option.iter _.Dispose()
        }
