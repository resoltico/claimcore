module ClaimCore.Tests.RoleMeaningTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.ContractGeneration
open ClaimCore.Application
open ClaimCore.Application.Contracts

let private replace key text source =
    source
    |> Map.change
        "en"
        (Option.map (List.map (fun (name, value) -> name, if name = key then text else value)))

let private rejects fragment action =
    let mutable message = ""

    try
        action ()
    with :? InvalidOperationException as error ->
        message <- error.Message

    Expect.stringContains message fragment "The intended publication gate detects the control"

[<Tests>]
let tests =
    testList
        "core role meanings"
        [
            testCase "every role publishes one translated meaning identity" (fun () ->
                let expected =
                    set
                        [
                            "OWNER"
                            "CASE_READER"
                            "CASE_REGISTRAR"
                            "CASE_HANDLER"
                            "PAYMENT_RECORDER"
                            "DATA_STEWARD"
                            "AUDITOR"
                        ]

                Expect.equal
                    (Catalog.document.Roles |> List.map (_.Role >> Role.token) |> Set.ofList)
                    expected
                    "The independent seven-role inventory is complete"

                let authored = AuthoredMessages.all ()

                for role in Catalog.document.Roles do
                    let key = "role." + Role.token role.Role + ".meaning"
                    Expect.equal role.MeaningId key "Metadata owns the meaning identity"

                    for language in [ "en"; "lv"; "ar" ] do
                        let text = authored[language] |> List.find (fst >> (=) key) |> snd

                        Expect.isFalse
                            (String.IsNullOrWhiteSpace text)
                            "Meaning is authored in each language")
            testCase
                "missing role meanings stop publication through the existing catalog owner"
                (fun () ->
                    let missing =
                        AuthoredMessages.all ()
                        |> Map.map (fun _ entries ->
                            entries |> List.filter (fst >> ((<>) "role.OWNER.meaning")))

                    rejects "Unknown core presentation identity" (fun () ->
                        CatalogValidation.validateCatalogs missing |> ignore))
            testCase
                "role meanings use GUI copy rules with only the role scope exception"
                (fun () ->
                    let source = AuthoredMessages.all ()

                    source
                    |> replace "role.OWNER.meaning" "Read the current snapshot."
                    |> fun entries ->
                        rejects "Implementation terminology" (fun () ->
                            MessageRegisters.validateCopy entries)

                    source
                    |> replace "role.OWNER.meaning" "Reviews role assignments in this scope."
                    |> MessageRegisters.validateCopy

                    source
                    |> replace "field.claimantName.hint" "Read the selected scope."
                    |> fun entries ->
                        rejects "Scope outside role" (fun () ->
                            MessageRegisters.validateCopy entries))
            testCase "copy states the scope and principal limits of the actual policy" (fun () ->
                let english = AuthoredMessages.all () |> Map.find "en" |> Map.ofList
                let meaning role = english["role." + role + ".meaning"]

                for role in [ Role.CaseRegistrar; Role.CaseHandler ] do
                    Expect.contains
                        (Access.policy role)
                        Capability.Register
                        "Role includes registration facts"

                let principal =
                    PrincipalKey.human "https://roles.example/" "scoped-handler"
                    |> Result.defaultWith (string >> failtest)

                let actorId =
                    Guid.Parse "13100000-0000-4000-8000-000000000001"
                    |> ActorId.create
                    |> Option.get

                let caseId =
                    Guid.Parse "13100000-0000-4000-8000-000000000002"
                    |> CaseId.create
                    |> Option.get

                for role in [ Role.CaseRegistrar; Role.CaseHandler ] do
                    let scoped =
                        AccessView.ofActor (
                            Some
                                {
                                    Actor =
                                        {
                                            ActorId = actorId
                                            Principal = principal
                                            Enabled = true
                                            Profile = None
                                        }
                                    Grants =
                                        [
                                            {
                                                Role = role
                                                Scope = Scope.Case caseId
                                            }
                                        ]
                                }
                        )

                    Expect.isTrue
                        (Access.can principal scoped Capability.Register (Scope.Case caseId))
                        "Case-scoped registration rights still permit edits to that existing case"

                    Expect.isFalse
                        (Advice.installationOffers principal scoped
                         |> List.contains InstallationOffer.Open)
                        "Opening is an installation offer, not the shared registration capability in any scope"

                Expect.stringContains
                    (meaning "CASE_REGISTRAR")
                    "this role for all cases"
                    "Registrar limitation"

                Expect.stringContains
                    (meaning "CASE_HANDLER")
                    "this role for all cases"
                    "Handler limitation"

                Expect.isTrue
                    (Access.humanOnlyCapability Capability.ManageCaseWork)
                    "Services cannot manage work"

                Expect.stringContains
                    (meaning "CASE_HANDLER")
                    "Only people manage"
                    "Work is limited to people"

                let people principal role scope =
                    AccessView.ofActor (
                        Some
                            {
                                Actor =
                                    {
                                        ActorId = actorId
                                        Principal = principal
                                        Enabled = true
                                        Profile = None
                                    }
                                Grants = [ { Role = role; Scope = scope } ]
                            }
                    )

                let service =
                    PrincipalKey.service "https://roles.example/" "service-handler"
                    |> Result.defaultWith (string >> failtest)

                let handler = people service Role.CaseHandler (Scope.Case caseId)

                Expect.isTrue
                    (Access.can service handler Capability.Decide (Scope.Case caseId))
                    "The service handler retains ordinary decision rights"

                Expect.isFalse
                    (Access.can service handler Capability.ManageCaseWork (Scope.Case caseId))
                    "The same service handler cannot manage responsibility or reminders"

                let caseAuditor = people principal Role.Auditor (Scope.Case caseId)

                Expect.isTrue
                    (Access.can
                        principal
                        caseAuditor
                        Capability.ReadHistoryFull
                        (Scope.Case caseId))
                    "A case auditor reads that case's full history"

                Expect.isFalse
                    (Access.can principal caseAuditor Capability.ReadAccess Scope.Installation)
                    "A case auditor cannot review account access"

                let installationAuditor = people principal Role.Auditor Scope.Installation

                Expect.isTrue
                    (Access.can
                        principal
                        installationAuditor
                        Capability.ReadAccess
                        Scope.Installation)
                    "Installation auditing includes people review"

                Expect.stringContains
                    (meaning "AUDITOR")
                    "With this role for all cases"
                    "People review scope limitation"

                Expect.stringContains
                    (meaning "CASE_REGISTRAR")
                    "Records and corrects claim details"
                    "Registrar editing purpose"

                Expect.equal
                    (Access.policy Role.Owner |> Set.ofList)
                    (set [ Capability.ManageAccess; Capability.ReadAccess ])
                    "Owner alone has no case-fact permission"

                Expect.stringContains
                    (meaning "OWNER")
                    "Does not give access to cases"
                    "Owner limitation"

                Expect.isTrue (Access.humanOnlyRole Role.DataSteward) "Steward is human only"
                Expect.stringContains (meaning "DATA_STEWARD") "For people" "Steward limitation"

                Expect.isFalse
                    (Access.policy Role.Auditor |> List.contains Capability.ManageAccess)
                    "Auditor cannot manage access"

                Expect.stringContains (meaning "AUDITOR") "Cannot change" "Read-only audit meaning")
        ]
