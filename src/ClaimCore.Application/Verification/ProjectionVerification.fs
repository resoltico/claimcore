namespace ClaimCore.Application

open ClaimCore.Application.VerificationChecks

/// Typed owner products are compared without rebuilding any business rule.
module ProjectionVerification =
    let column finding get expected actual =
        require None finding (get expected = get actual)

    /// A row is compared without rebuilding or reinterpreting any business rule.
    let row finding key expected seen actual =
        let id = key actual

        require None finding (not (Set.contains id seen) && Map.tryFind id expected = Some actual)
        |> Result.map (fun () -> Set.add id seen)

    let finishRows finding expected seen =
        require None finding (Map.count expected = Set.count seen)

    let rows finding key expected actual =
        actual
        |> List.fold
            (fun seen actual ->
                seen |> Result.bind (fun seen -> row finding key expected seen actual))
            (Ok Set.empty)
        |> Result.bind (finishRows finding expected)
