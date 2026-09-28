module ClaimCore.Tests.ActorCommandCapabilityTests

open Expecto
open ClaimCore.Application
open ClaimCore.Domain

let private commandMatrix () =
    let all =
        [
            CommandKind.Open
            CommandKind.AmendRegistration
            CommandKind.CorrectCase
            CommandKind.Decide
            CommandKind.WithdrawDecision
            CommandKind.RecordPayment
            CommandKind.ClearPayment
            CommandKind.Close
            CommandKind.Reopen
        ]

    Expect.equal all.Length 9 "Every current Domain command is represented"

    for command in all do
        Expect.equal
            (ActorAuthorization.commandCapability command)
            Capability.EditCase
            "Every current Domain command requires case edit authority"

let tests =
    testCase "[CC-AUTH-001] every Domain command maps to edit capability" commandMatrix
