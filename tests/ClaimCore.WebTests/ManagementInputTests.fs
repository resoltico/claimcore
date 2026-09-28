module ClaimCore.WebTests.ManagementInputTests

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes(value)

let private managementInput () =
    let principal =
        """{"kind":"HUMAN","issuer":"https://issuer.example.test/realms/synthetic","subject":"synthetic-steward"}"""

    let valid =
        $"""{{"eventId":"40000000-0000-4000-8000-000000000001","principal":{principal},"role":"DATA_STEWARD","scope":{{"kind":"INSTALLATION"}},"active":true}}"""

    match HttpManagementInput.setGrant (bytes valid) with
    | Ok input ->
        Expect.equal
            input.EventId
            (Guid.Parse("40000000-0000-4000-8000-000000000001"))
            "Caller event ID is retained"

        Expect.equal input.Role Role.DataSteward "Role comes from the closed wire set"
        Expect.equal input.Scope GrantTarget.Installation "Scope is explicitly installation-wide"
        Expect.isTrue input.Active "Grant activation remains a Boolean"
    | Error _ -> failtest "The exact management grant shape must decode"

    let refused source =
        match HttpManagementInput.setGrant (bytes source) with
        | Ok _ -> failtest "Invalid management input was admitted"
        | Error _ -> ()

    refused (
        valid.Replace(
            "https://issuer.example.test",
            "http://issuer.example.test",
            StringComparison.Ordinal
        )
    )

    refused (valid.Replace("DATA_STEWARD", "INVENTED_ROLE", StringComparison.Ordinal))

    refused (
        valid.Replace(
            "\"eventId\":",
            "\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"eventId\":",
            StringComparison.Ordinal
        )
    )

    refused (
        valid.Replace(
            "\"scope\":{\"kind\":\"INSTALLATION\"}",
            "\"scope\":{\"kind\":\"INSTALLATION\",\"caseReference\":\"PRIVATE\"}",
            StringComparison.Ordinal
        )
    )

    refused (valid.Replace("\"active\":true", "\"active\":\"true\"", StringComparison.Ordinal))

let tests =
    testList
        "management input"
        [
            testCase
                "[CC-WEB-001] binds exact authority event, principal, role and scope before dispatch"
                managementInput
        ]
