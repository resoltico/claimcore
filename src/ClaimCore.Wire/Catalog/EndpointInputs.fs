namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Transport member spellings adapt semantic inputs once for codecs and generic acquisition.
module EndpointInputs =
    let private queryMember =
        function
        | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) -> "target"
        | TaskInputId.Target InputTarget.DirectorySearch -> "text"
        | TaskInputId.RoleFilter -> "role"
        | TaskInputId.DisclosureCaseId -> "caseId"
        | TaskInputId.DisclosureKind -> "kind"
        | TaskInputId.DisclosureFrom -> "from"
        | TaskInputId.DisclosureUntil -> "until"
        | TaskInputId.CaseView
        | TaskInputId.ReminderView -> "view"
        | TaskInputId.ResponsibleActorFilter -> "responsibleActor"
        | TaskInputId.TimelineFilter -> "filter"
        | TaskInputId.ReminderState -> "state"
        | TaskInputId.ReminderDue -> "due"
        | TaskInputId.ReminderKindFilter -> "kind"
        | TaskInputId.DeadlineOutcomeFilter -> "deadlineOutcome"
        | input -> TaskInputId.key input

    let memberName (declaration: EndpointDeclaration) input =
        match declaration.Kind with
        | EndpointKind.Command -> TaskInputId.token input
        | EndpointKind.Query
        | EndpointKind.Binary -> queryMember input

    /// Codec structure and use-case declarations independently describe the same request boundary.
    let check id (codec: Codec<'input>) =
        let declaration =
            EndpointDeclarations.find id
            |> Option.defaultWith (fun () -> invalidArg (nameof id) "The endpoint is not declared.")

        let rec resolve =
            function
            | Schema.Ref name -> codec.Definitions[name] |> resolve
            | Schema.Vocabulary(_, shape) -> resolve shape
            | Schema.OneOf(variants, _) as shape ->
                match variants |> List.filter ((<>) Schema.Null) with
                | [ single ] -> resolve single
                | _ -> shape
            | shape -> shape

        let properties =
            match resolve codec.Schema with
            | Schema.Object(properties, _) -> properties |> Map.ofList
            | _ -> invalidOp (id + " requires an object request.")

        let names =
            declaration.Inputs |> List.map (fun input -> memberName declaration input.Input)

        if
            (Set.ofList names).Count <> names.Length
            || Set.ofList names <> (properties |> Map.keys |> Set.ofSeq)
        then
            invalidOp (id + " codec members disagree with its endpoint declaration.")

        for input in declaration.Inputs do
            let shape = properties[memberName declaration input.Input] |> resolve

            let multiple =
                match shape with
                | Schema.Array _ -> true
                | _ -> false

            if multiple <> input.Multiple then
                invalidOp (id + " input cardinality disagrees with its endpoint declaration.")

        match declaration.Page with
        | None -> ()
        | Some page ->
            match resolve properties["limit"], resolve properties["cursor"] with
            | Schema.Integer(1L, maximum), Schema.String(_, _, _, Some cursor, _) when
                maximum = int64 page.MaximumLimit
                && cursor = page.MaximumCursorCharacters
                && page.DefaultLimit >= 1
                && page.DefaultLimit <= page.MaximumLimit
                ->
                ()
            | _ -> invalidOp (id + " paging bounds disagree with its endpoint declaration.")

        codec
