module internal ClaimCore.WebTests.CliIdentityClient

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Cli
open ClaimCore.ClientContract

let private privateText directory name (text: string) =
    let path = Path.Combine(directory, name)
    File.WriteAllText(path, text)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path

let private serviceReply service (token: AccessToken) cancelled =
    task {
        let invocation =
            Frames.parse (
                System.Text.Encoding.UTF8.GetBytes(
                    """{"endpoint":"case.get","input":{"caseReference":"SYNTHETIC-001"}}"""
                )
            )
            |> Result.defaultWith (fun _ -> failtest "Frame refused.")

        let! reply =
            RemoteServiceCall.send
                service
                (Uri "https://localhost:5443/")
                invocation
                token.Authorization
                ignore
                cancelled

        Expect.equal reply.ExitCode 0 "CLI token is accepted by real issuer validation at Web"
        Expect.isOk (Frames.decode reply.Bytes) "Service result is decoded by CLI"
    }

let qualify (server: IdentityServer.Identity) (service: HttpClient) (cancelled: CancellationToken) =
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
            let! metadata = OidcClient.discover identity issuer cancelled

            let endpoints =
                metadata |> Result.defaultWith (fun _ -> failtest "Live discovery refused.")

            let! acquired =
                OAuthTokenClient.acquire
                    identity
                    endpoints
                    "service"
                    (OAuthGrant.ClientCredentials secretPath)
                    cancelled

            let token =
                acquired
                |> Result.defaultWith (fun _ -> failtest "Live client credentials refused.")

            do! serviceReply service token cancelled
            File.WriteAllText(secretPath, "invalid-synthetic-secret")

            let! denied =
                OAuthTokenClient.acquire
                    identity
                    endpoints
                    "service"
                    (OAuthGrant.ClientCredentials secretPath)
                    cancelled

            Expect.isError denied "Wrong client secret refused by real provider"
        finally
            Directory.Delete(directory, true)
    }
