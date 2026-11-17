module internal ClaimCore.ServerTests.CliIdentityClient

open ClaimCore.TestSupport

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Cli

let private privateText directory name (text: string) =
    let path = Path.Combine(directory, name)
    File.WriteAllText(path, text)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path

let private serviceReply service installationId (token: AccessToken) stage cancelled =
    task {
        let invocation =
            Frames.admit (
                System.Text.Encoding.UTF8.GetBytes(
                    """{"endpoint":"case.get","input":{"target":{"kind":"REFERENCE","caseReference":"SYNTHETIC-001"}}}"""
                )
            )
            |> Result.defaultWith (fun _ -> failtest "Frame refused.")

        let send suppliedInstallation =
            RemoteServiceCall.send
                service
                {
                    InstallationId = Some suppliedInstallation
                    Service = (Uri "https://localhost:5443/")
                    Authorization = token.Authorization
                }
                invocation
                ignore
                cancelled

        let fixtureMarker = Guid.Parse "90000000-0000-4000-8000-000000000001"

        let foreignInstallation =
            if installationId = fixtureMarker then
                Guid.Parse "90000000-0000-4000-8000-000000000002"
            else
                fixtureMarker

        stage "CLI supplied foreign installation refusal"
        let! refused = send foreignInstallation
        Expect.equal refused.ExitCode 3 "A supplied foreign installation is a definite refusal"

        match Frames.decode refused.Bytes with
        | Ok {
                 Result = FrameResult.HostFailure failure
             } ->
            Expect.equal
                failure.Code
                HostFailureCode.InstallationMismatch
                "A valid issuer token cannot bypass the installation binding"
        | _ -> failtest "Expected the typed installation-mismatch host failure"

        let! reply = send installationId

        let category =
            match Frames.decode reply.Bytes with
            | Ok {
                     Result = FrameResult.HostFailure failure
                 } -> sprintf "HOST_%A" failure.Code
            | Ok { Result = FrameResult.LocalFailure _ } -> "LOCAL_FAILURE"
            | Ok { Result = FrameResult.Reply _ } -> "SERVICE_REPLY"
            | Error _ -> "FRAME_INVALID"

        stage (sprintf "CLI service dispatch: exit=%d category=%s" reply.ExitCode category)
        Expect.equal reply.ExitCode 0 "CLI token is accepted by real issuer validation at Web"
        Expect.isOk (Frames.decode reply.Bytes) "Service result is decoded by CLI"
    }

let qualify
    (server: IdentityServer.Identity)
    (service: HttpClient)
    installationId
    stage
    (cancelled: CancellationToken)
    =
    task {
        let directory = Directory.CreateTempSubdirectory("claimcore-cli-identity-").FullName

        try
            use credentials = JsonDocument.Parse server.Credentials

            let secret =
                credentials.RootElement.GetProperty("serviceClientSecret").GetString()
                |> nonNull

            let secretPath = privateText directory "client-secret" secret

            let rootPath =
                privateText directory "issuer-root.pem" (server.Root.ExportCertificatePem())

            let issuer = Uri server.Issuer

            let trust =
                RemoteTls.Create(Some rootPath, issuer)
                |> Result.defaultWith (fun _ -> failtest "Owned root refused.")

            use _owned = trust :> IDisposable
            use identity = new HttpClient(trust.Handler, false)
            stage "CLI issuer discovery"
            let! metadata = OidcClient.discover identity issuer cancelled

            let endpoints =
                metadata |> Result.defaultWith (fun _ -> failtest "Live discovery refused.")

            stage "CLI token acquisition"

            let! acquired =
                OAuthTokenClient.acquire
                    identity
                    (Uri(endpoints.TokenEndpoint |> nonNull))
                    "service"
                    secretPath
                    cancelled

            let token =
                acquired
                |> Result.defaultWith (fun _ -> failtest "Live client credentials refused.")

            do! serviceReply service installationId token stage cancelled
            File.WriteAllText(secretPath, "invalid-synthetic-secret")

            stage "CLI wrong-secret refusal"

            let! denied =
                OAuthTokenClient.acquire
                    identity
                    (Uri(endpoints.TokenEndpoint |> nonNull))
                    "service"
                    secretPath
                    cancelled

            Expect.isError denied "Wrong client secret refused by real provider"
        finally
            Directory.Delete(directory, true)
    }
