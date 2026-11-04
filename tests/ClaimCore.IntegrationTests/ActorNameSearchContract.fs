module ClaimCore.TestSupport.ActorNameSearchContract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport.StoreFixtures

let private required result =
    result
    |> Result.defaultWith (fun error -> failtestf "Name search fixture: %A" error)

let private success =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Name search: %A" other

let private request text cursor limit =
    {
        ActorListRequest.Text = text
        AfterCursor = cursor
        Limit = limit
    }

let private search owner text =
    Service.actors owner (request (Some text) None 50) System.Threading.CancellationToken.None

let internal lookup fixture =
    task {
        let token = System.Threading.CancellationToken.None
        let! owner = bootstrap fixture

        let names =
            [
                "Alice Example"
                "Āna Bērziņa"
                "ليلى العربية"
                "İstanbul"
                "ς"
                "Straße"
                "Ａｌｉｃｅ"
                "e\u0301"
                "Percent% under_score\\slash"
                "Wildcard other"
            ]

        let mutable people = []

        for index, name in List.indexed names do
            let principal =
                PrincipalKey.human "https://issuer.example/" ($"name-person-{index}")
                |> required

            let profile =
                {
                    ActorProfileInput.DisplayName = name
                    Contact = Some "contact-only-needle"
                }

            let! result =
                Service.execute
                    owner
                    (OperationInput.Authority(
                        Inputs.authorityRequest
                            (id (1100 + index))
                            principal
                            (AuthorityAct.Register profile)
                    ))
                    token

            acceptedAuthority result
            people <- (name, principal) :: people

        let expectations =
            [
                "ALICE", [ "Alice Example"; "Ａｌｉｃｅ" ]
                "āNA", [ "Āna Bērziņa" ]
                "العربية", [ "ليلى العربية" ]
                "İSTANBUL", [ "İstanbul" ]
                "ς", [ "ς" ]
                "é", [ "e\u0301" ]
                "%", [ "Percent% under_score\\slash" ]
                "_", [ "Percent% under_score\\slash" ]
                "\\", [ "Percent% under_score\\slash" ]
                "istanbul", []
                "Σ", []
                "STRASSE", []
            ]
            |> List.map (fun (query, names) -> query, Set.ofList names)
            |> Map.ofList

        let expected query = Map.find query expectations

        for query in
            [
                "ALICE"
                "āNA"
                "العربية"
                "İSTANBUL"
                "istanbul"
                "Σ"
                "ς"
                "STRASSE"
                "é"
                "%"
                "_"
                "\\"
            ] do
            let! result = search owner query

            let actual =
                (success result).Items
                |> List.map (fun person ->
                    match person.Actor.DisplayName with
                    | DisplayName.Named name -> PartyName.format name
                    | DisplayName.Erased -> failtest "Erased names cannot satisfy a name predicate")
                |> Set.ofList

            Expect.equal
                actual
                (expected query)
                ($"Domain-normalized literal matching is identical for {query}")

        let! privateFields = search owner "contact-only-needle"
        Expect.isEmpty (success privateFields).Items "Contact is not a name-search side channel"
        let! subject = search owner "name-person-"
        Expect.isEmpty (success subject).Items "Principal identifiers are not searched"
        let! first = Service.actors owner (request (Some "ALICE") None 1) token
        let first = success first
        Expect.equal first.Items.Length 1 "Filtered page uses server limit"

        let cursor =
            first.NextCursor
            |> Option.defaultWith (fun () -> failtest "Two normalized Alice names need a cursor")

        let! next = Service.actors owner (request (Some "ＡＬＩＣＥ") (Some cursor) 1) token
        Expect.equal (success next).Items.Length 1 "Equivalent normalized filters share a cursor"
        let! changed = Service.actors owner (request (Some "Bērziņa") (Some cursor) 1) token

        Expect.equal
            changed
            (Outcome.Rejected Refusal.InvalidCursor)
            "Changing the filter cannot reuse a cursor"

        let! omitted = Service.actors owner (request None (Some cursor) 1) token

        Expect.equal
            omitted
            (Outcome.Rejected Refusal.InvalidCursor)
            "Filtered cursor cannot become an unfiltered scan"

        let person = people |> List.find (fun (name, _) -> name = "Alice Example") |> snd

        let! erased =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest (id 1120) person AuthorityAct.EraseProfile
                ))
                token

        acceptedAuthority erased
        let! after = search owner "ALICE"

        Expect.equal
            ((success after).Items |> List.map _.Principal)
            [ people |> List.find (fun (name, _) -> name = "Ａｌｉｃｅ") |> snd ]
            "Erased profiles immediately stop matching while distinct authored compatibility spelling remains"

        let! restored =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id 1121)
                        person
                        (AuthorityAct.SetProfile
                            {
                                DisplayName = "Renamed person"
                                Contact = None
                            })
                ))
                token

        acceptedAuthority restored
        let! old = search owner "Alice Example"
        Expect.isEmpty (success old).Items "Reprofile cannot retain an obsolete search value"
        let! fresh = search owner "RENAMED"

        Expect.equal
            ((success fresh).Items |> List.map _.Principal)
            [ person ]
            "Reprofile derives only the current authored name"

        let! empty = search owner ""

        match empty with
        | Outcome.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.head issues).Target
                InputTarget.ActorNameSearch
                "Name admission has its own semantic target"
        | other -> failtestf "Empty filter must refuse: %A" other
    }
