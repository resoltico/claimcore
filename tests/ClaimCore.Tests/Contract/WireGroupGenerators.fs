module ClaimCore.Tests.WireGroupGenerators

open System
open ClaimCore.Domain
open ClaimCore.TestSupport
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.WireScalarGenerators

let private members group admitted =
    Inventory.fields group
    |> List.map (fun row ->
        let token = row.GetProperty("key").GetString() |> nonNull
        let field = Vocabulary.tryParse BusinessField.vocabulary token |> Option.get
        let kind = row.GetProperty("kind").GetString() |> nonNull

        let generator =
            if not admitted then
                arbitraryText
            else
                match kind with
                | "DATE" -> date
                | "COUNTRY" -> uppercase 2
                | "CURRENCY" -> uppercase 3
                | "AMOUNT" -> amount
                | "NAME" -> scalarText
                | _ -> invalidOp "The independent group inventory requires a value generator."

        generator |> Gen.map (fun value -> field, value))
    |> sequence
    |> Gen.map (fun values ->
        if admitted && group = "registration" then
            let fields = Map.ofList values
            let incident = fields[BusinessField.IncidentDate]
            let notification = fields[BusinessField.IncidentNotificationDate]

            values
            |> List.map (fun (field, value) ->
                field,
                match field with
                | BusinessField.IncidentDate -> min incident notification
                | BusinessField.IncidentNotificationDate -> max incident notification
                | _ -> value)
        else
            values)

let groupValue (kind: Type) =
    if kind = typeof<RegistrationInput> then
        Some(members "registration" false |> Gen.map (Inputs.registrationFrom >> box))
    elif kind = typeof<DecisionInput> then
        Some(members "decision" false |> Gen.map (Inputs.decisionFrom >> box))
    elif kind = typeof<PaymentInput> then
        Some(members "payment" false |> Gen.map (Inputs.paymentFrom >> box))
    elif kind = typeof<Registration> then
        Some(
            members "registration" true
            |> Gen.map (Inputs.registrationFrom >> Inputs.admittedRegistration >> box)
        )
    elif kind = typeof<Decision> then
        Some(
            members "decision" true
            |> Gen.map (Inputs.decisionFrom >> Inputs.admittedDecision >> box)
        )
    elif kind = typeof<Payment> then
        Some(
            members "payment" true
            |> Gen.map (Inputs.paymentFrom >> Inputs.admittedPayment >> box)
        )
    else
        None
