module ClaimCore.AcceptanceTests.RemoteAuthAcceptance

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto

let private ownerMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private refusedToken () =
    let wrongSecret =
        Path.Combine(
            RemoteFixture.inputs.Value.PrivateDirectory,
            "wrong-service-secret-" + RemoteFixture.id ()
        )

    use stream =
        new FileStream(
            wrongSecret,
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = Nullable ownerMode
            )
        )

    stream.Write(Encoding.UTF8.GetBytes("synthetic-invalid-client-secret"))
    stream.Flush(true)
    let rejected = RemoteFixture.withSecret wrongSecret "case.list" {| limit = 1 |}
    Expect.equal rejected.ExitCode 3 "Real issuer rejects the wrong client secret"
    use tokenFailure = JsonDocument.Parse(ReadOnlyMemory rejected.StandardOutput)

    Expect.equal
        (tokenFailure.RootElement.GetProperty("code").GetString())
        "CLI_AUTHENTICATION_UNAVAILABLE"
        "No service call after token refusal"

let forgedPkceCallback () =
    let response = RemoteFixture.interactive "wrong-state" "case.list" {| limit = 1 |}
    Expect.equal response.ExitCode 3 "Forged callback cannot obtain a token"
    Expect.equal response.StandardError.Length 0 "No callback data in process diagnostics"
    use document = JsonDocument.Parse(ReadOnlyMemory response.StandardOutput)

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "localFailure"
        "Typed refusal"

    refusedToken ()
