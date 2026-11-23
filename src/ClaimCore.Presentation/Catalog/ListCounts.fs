namespace ClaimCore.Presentation

/// Complete sentences for each plural category. Renderers format the count in their requested locale.
module ListCounts =
    let messages =
        [ "cases"; "reminders"; "people" ]
        |> List.map (fun subject ->
            subject,
            ([ "zero"; "one"; "two"; "few"; "many"; "other" ]
             |> List.map (fun category -> category, "label.shown." + subject + "." + category)
             |> Map.ofList))
        |> Map.ofList
