module ClaimCore.Tests.InteractionBindingTests

open Expecto
open ClaimCore.Contract
open ClaimCore.ContractGeneration

let private alter transform =
    let first = List.head InteractionDefinitions.all
    transform first :: List.tail InteractionDefinitions.all

let private rejects values =
    Expect.throws
        (fun () -> BindingValidation.validate values)
        "Invalid interaction bindings refuse before emission"

let private pathAndCoverage () =
    BindingValidation.validate InteractionDefinitions.all

    alter (fun interaction ->
        { interaction with
            Inputs = List.tail interaction.Inputs
        })
    |> rejects

    alter (fun interaction ->
        { interaction with
            Inputs =
                interaction.Inputs
                |> List.map (fun input ->
                    { input with
                        Path = "missing" :: input.Path
                    })
        })
    |> rejects

    alter (fun interaction ->
        { interaction with
            Inputs = List.head interaction.Inputs :: interaction.Inputs
        })
    |> rejects

    alter (fun interaction ->
        { interaction with
            Inputs =
                interaction.Inputs
                |> List.map (fun input ->
                    { input with
                        Kind = BindingKind.Boolean
                    })
        })
    |> rejects

let private exactBranches () =
    let correction =
        InteractionDefinitions.all |> List.find (fun item -> item.Kind = "CORRECT_CASE")

    let decision =
        correction.Inputs
        |> List.find (fun input -> input.DiagnosticTargets = [ "payableAmount" ])

    Expect.equal
        decision.Path
        [ "act"; "groups"; "decision"; "values"; "payableAmount" ]
        "Whole nested path is explicit"

    let wrong =
        { correction with
            Inputs =
                correction.Inputs
                |> List.map (fun input ->
                    if input.Identity = decision.Identity then
                        { input with Branches = [] }
                    else
                        input)
        }

    rejects [ wrong ]

    let authority =
        InteractionDefinitions.all |> List.find (fun item -> item.Scope = "AUTHORITY")

    let issuer =
        authority.Inputs
        |> List.find (fun input -> input.DiagnosticTargets = [ "principal.issuer" ])

    Expect.equal
        issuer.Path
        [ "target"; "issuer" ]
        "Diagnostic identity binds the declared principal member"

let private grantReference () =
    let grant =
        InteractionDefinitions.all |> List.find (fun item -> item.Kind = "SET_GRANT")

    let reference =
        grant.Inputs
        |> List.find (fun input -> input.Path = [ "act"; "scope"; "caseReference" ])

    Expect.equal
        reference.Field
        (Some(InputTarget.Field BusinessField.CaseReference))
        "Shared field semantics"

    Expect.equal reference.LabelId "field.caseReference.label" "Shared reference label"
    Expect.equal reference.MeaningId (Some "field.caseReference.meaning") "Shared reference meaning"

    Expect.equal
        reference.DiagnosticTargets
        [ "caseReference" ]
        "Domain refusal targets the exact nested input"

    Expect.equal
        reference.Branches
        [
            {
                Path = [ "act"; "scope"; "kind" ]
                Value = "CASE"
            }
        ]
        "Installation grants do not expose a case input"

let private aliases () =
    let first = List.head InteractionDefinitions.all
    rejects [ first; first ]

    let authority =
        InteractionDefinitions.all |> List.find (fun item -> item.Kind = "SET_ENABLED")

    let invalidConstant =
        {
            Path = [ "act"; "enabled" ]
            Value = BindingValue.Text "wrong"
        }

    rejects
        [
            { authority with
                Constants = authority.Constants @ [ invalidConstant ]
            }
        ]

    let roundtrip = ContractDescriptors.valueFrom ContractDescriptors.defaults

    Expect.equal
        (Codec.encode DescriptorCodecs.codec roundtrip
         |> Codec.decode 4194304 DescriptorCodecs.codec)
        (Ok roundtrip)
        "Typed descriptor read/write preserves all binding identities"

[<Tests>]
let tests =
    testList
        "exact interaction bindings"
        [
            testCase "missing, invalid, duplicate and mismatched bindings reject" pathAndCoverage
            testCase "correction branches and principal diagnostics bind exact paths" exactBranches
            testCase
                "case-scoped grant reference retains shared field and diagnostic semantics"
                grantReference
            testCase
                "duplicate definitions and invalid constants refuse and descriptors round trip"
                aliases
        ]
