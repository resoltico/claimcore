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

let private aliases () =
    let first = List.head InteractionDefinitions.all
    rejects [ first; first ]

    let authority =
        InteractionDefinitions.all |> List.find (fun item -> item.Kind = "SET_ENABLED")

    rejects
        [
            { authority with
                Invocations =
                    authority.Invocations |> List.map (fun alias -> { alias with Constants = [] })
            }
        ]

    rejects
        [
            { authority with
                Invocations =
                    authority.Invocations
                    |> List.map (fun alias ->
                        { alias with
                            Constants =
                                [
                                    {
                                        Path = [ "act"; "enabled" ]
                                        Value = BindingValue.Text "wrong"
                                    }
                                ]
                        })
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
            testCase "ambiguous aliases refuse and descriptor codecs round trip" aliases
        ]
