module ClaimCore.Tests.ShellAuthenticationTests

open System
open System.Threading
open ClaimCore.Cli
open Expecto
open ShellIdentityFixture
open ShellTestClock

let private reuse () =
    let clock = Clock()
    use provider = new Provider(clock)
    use credentials = session provider
    let first = token credentials

    for _ in 1..5 do
        Expect.isTrue
            (obj.ReferenceEquals(first, token credentials))
            "Commands share the same admitted token"

    Expect.equal provider.SignIns 1 "Only one real PKCE exchange"
    Expect.equal provider.Refreshes 0 "Unexpired token needs no refresh"
    provider.Completed().GetAwaiter().GetResult()

let private rotation () =
    let clock = Clock()
    use provider = new Provider(clock)
    use credentials = session provider
    token credentials |> ignore

    for _ in 1..2 do
        clock.Advance(TimeSpan.FromSeconds 30.)
        let renewed = token credentials
        Expect.isTrue renewed.CanReuse "Fresh token uses its own conservative lifetime"

    Expect.equal provider.SignIns 1 "Expiry refreshes without opening a browser"
    Expect.equal provider.Refreshes 2 "Second refresh proves the rotated token is retained"
    provider.Completed().GetAwaiter().GetResult()

let private omittedRotation () =
    let clock = Clock()
    use provider = new Provider(clock)
    use credentials = session provider
    token credentials |> ignore
    provider.RotateRefresh false

    for _ in 1..2 do
        clock.Advance(TimeSpan.FromSeconds 30.)
        token credentials |> ignore

    Expect.equal provider.Refreshes 2 "Omitted refresh token retains the prior grant token"
    Expect.equal provider.SignIns 1 "No unnecessary browser sign-in"
    provider.Completed().GetAwaiter().GetResult()

let private failureAndDiscard () =
    let clock = Clock()
    use provider = new Provider(clock)
    use credentials = session provider
    token credentials |> ignore
    clock.Advance(TimeSpan.FromSeconds 30.)
    provider.FailRefresh()
    token credentials |> ignore
    Expect.equal provider.Refreshes 1 "Real invalid_grant response is exercised"
    Expect.equal provider.SignIns 2 "Refresh failure discards the grant and signs in again"
    credentials.Discard(CancellationToken.None).GetAwaiter().GetResult()
    token credentials |> ignore
    Expect.equal provider.SignIns 3 "Idle discard removes both access and refresh material"
    Expect.equal provider.Refreshes 1 "A discarded refresh token cannot be reused"
    provider.Completed().GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "shell authentication"
        [
            testCase "many commands share one real PKCE sign-in" reuse
            testCase "expiry refreshes and rotates the memory-only token" rotation
            testCase "refresh omission retains the current token" omittedRotation
            testCase "refresh failure signs in again and discard prevents reuse" failureAndDiscard
        ]
