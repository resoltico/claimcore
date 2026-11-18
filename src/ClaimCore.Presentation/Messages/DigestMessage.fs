namespace ClaimCore.Presentation

open System
open System.Globalization
open ClaimCore.Domain

module DigestMessage =
    let subjectKey = "digest.subject"
    let linkKey = "digest.link"

    let countKey kind =
        Vocabulary.messageKey DigestCountKind.vocabulary kind "line"

    let contracts =
        [
            subjectKey, Set.singleton "count"
            linkKey, Set.singleton "url"
            for kind in Vocabulary.all DigestCountKind.vocabulary do
                countKey kind, Set.singleton "count"
        ]

    let format catalog language (link: Uri) (counts: Map<DigestCountKind, int64>) =
        let culture =
            CultureInfo.GetCultureInfo((LocalizationPolicy.languageOption language).displayLocale)

        let number (value: int64) = value.ToString("N0", culture)

        let render key name value =
            Messages.render catalog key (Map.ofList [ name, value ])

        let subject = render subjectKey "count" (Map.values counts |> Seq.sum |> number)

        let body =
            [
                for kind in Vocabulary.all DigestCountKind.vocabulary do
                    match Map.tryFind kind counts with
                    | Some count when count > 0L -> render (countKey kind) "count" (number count)
                    | _ -> ()
                render linkKey "url" link.AbsoluteUri
            ]
            |> String.concat "\n"

        subject, body
