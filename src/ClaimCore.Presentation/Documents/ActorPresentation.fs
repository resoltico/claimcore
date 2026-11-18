namespace ClaimCore.Presentation

open ClaimCore.Domain

/// The core owns actor naming; renderers apply only their text escaping and bidi treatment.
module ActorPresentation =
    let formerAccountKey = "label.formerAccount"

    let shortId (actor: ActorRef) =
        ActorId.format actor.ActorId |> fun value -> value[..7]

    let displayName render (actor: ActorRef) =
        match actor.DisplayName with
        | DisplayName.Named value -> PartyName.format value
        | DisplayName.Erased -> render formerAccountKey (Map.ofList [ "id", shortId actor ])

    let operatorLabel = "label.installationOperator"

    let authorityValue =
        function
        | AuthorityRef.Actor actor -> PresentedValue.Actor actor
        | AuthorityRef.Operator -> PresentedValue.Message operatorLabel

    let authorityName render =
        function
        | AuthorityRef.Actor actor -> displayName render actor
        | AuthorityRef.Operator -> render operatorLabel Map.empty
