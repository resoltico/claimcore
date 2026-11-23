namespace ClaimCore.Presentation

/// Complete sentences for each plural category. Renderers format the count in their requested locale.
module ListCounts =
    let private messagesFor prefix subject =
        subject,
        ([ "zero"; "one"; "two"; "few"; "many"; "other" ]
         |> List.map (fun category -> category, prefix + subject + "." + category)
         |> Map.ofList)

    let messages =
        [ "cases"; "reminders"; "people" ]
        |> List.map (messagesFor "label.shown.")
        |> Map.ofList

    let sectionMessages =
        [ "payments"; "notes"; "attachments"; "activity"; "accessRequests" ]
        |> List.map (messagesFor "label.count.")
        |> Map.ofList
