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
        ActorListRequest.Role = None
        Text = text
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
                    (OperationInput.People(
                        Inputs.peopleRequest
                            (id fixture (1100 + index))
                            principal
                            (PeopleAct.Register profile)
                    ))
                    token

            acceptedPeople result
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

        Expect.equal
            (success privateFields).Items.Length
            names.Length
            "W9 text covers current saved contact details as well as names"

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
                (OperationInput.People(
                    Inputs.peopleRequest (id fixture 1120) person PeopleAct.EraseProfile
                ))
                token

        acceptedPeople erased
        let! after = search owner "ALICE"

        Expect.equal
            ((success after).Items |> List.map _.Principal)
            [ people |> List.find (fun (name, _) -> name = "Ａｌｉｃｅ") |> snd ]
            "Erased profiles immediately stop matching while distinct authored compatibility spelling remains"

        let! restored =
            Service.execute
                owner
                (OperationInput.People(
                    Inputs.peopleRequest
                        (id fixture 1121)
                        person
                        (PeopleAct.SetProfile
                            {
                                DisplayName = "Renamed person"
                                Contact = None
                            })
                ))
                token

        acceptedPeople restored
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
                InputTarget.DirectorySearch
                "Name admission has its own semantic target"
        | other -> failtestf "Empty filter must refuse: %A" other
    }

let internal contactAndRole fixture =
    task {
        let token = System.Threading.CancellationToken.None
        let! owner = bootstrap fixture

        let make operation subject display contact =
            task {
                let principal = PrincipalKey.human "https://issuer.example/" subject |> required

                let! reply =
                    Service.execute
                        owner
                        (OperationInput.People(
                            Inputs.peopleRequest
                                (id fixture operation)
                                principal
                                (PeopleAct.Register
                                    {
                                        DisplayName = display
                                        Contact = contact
                                    })
                        ))
                        token

                acceptedPeople reply
                return principal
            }

        let! first =
            make 1140 "directory-contact-a" "Other person" (Some "ĀNA%_\\contact@example.invalid")

        let! second =
            make 1141 "directory-contact-b" "Āna named person" (Some "different@example.invalid")

        let! third = make 1142 "directory-contact-c" "Unrelated name" None

        for operation, person, role in
            [
                1143, first, Role.CaseReader
                1144, second, Role.Auditor
                1145, third, Role.CaseReader
            ] do
            let! granted =
                grant
                    owner
                    (id fixture operation)
                    person
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople granted

        let find text role cursor limit =
            Service.actors
                owner
                {
                    ActorListRequest.Text = text
                    Role = role
                    AfterCursor = cursor
                    Limit = limit
                }
                token

        let! text = find (Some "āNA") None None 50

        Expect.equal
            ((success text).Items |> List.map _.Principal |> Set.ofList)
            (set[first
                 second])
            "Text searches name OR contact in the same server predicate"

        let! literal = find (Some "%_\\") None None 50

        Expect.equal
            ((success literal).Items |> List.map _.Principal)
            [ first ]
            "Contact wildcard characters stay literal"

        let! role = find None (Some Role.CaseReader) None 50

        Expect.equal
            ((success role).Items |> List.map _.Principal |> Set.ofList)
            (set[first
                 third])
            "Typed role selection uses recognized raw assignments"

        let! both = find (Some "āna") (Some Role.CaseReader) None 50

        Expect.equal
            ((success both).Items |> List.map _.Principal)
            [ first ]
            "Typed role and literal text compose before pagination"

        let! page = find None (Some Role.CaseReader) None 1

        let cursor =
            (success page).NextCursor
            |> Option.defaultWith (fun () -> failtest "Two role rows need a continuation")

        let! changed = find None (Some Role.Auditor) (Some cursor) 1

        Expect.equal
            changed
            (Outcome.Rejected Refusal.InvalidCursor)
            "A role cursor cannot change role"

        let! omitted = find None None (Some cursor) 1

        Expect.equal
            omitted
            (Outcome.Rejected Refusal.InvalidCursor)
            "A role cursor cannot become an unfiltered page"

        let! erased =
            Service.execute
                owner
                (OperationInput.People(
                    Inputs.peopleRequest (id fixture 1146) first PeopleAct.EraseProfile
                ))
                token

        acceptedPeople erased
        let! absent = find (Some "contact@example") None None 50

        Expect.isEmpty
            (success absent).Items
            "Erasure clears contact text and its search projection"

        let! kept = find None (Some Role.CaseReader) None 50

        Expect.equal
            ((success kept).Items |> List.map _.Principal |> Set.ofList)
            (set[first
                 third])
            "Profile erasure never removes assigned role facts"

        let! reprofiled =
            Service.execute
                owner
                (OperationInput.People(
                    Inputs.peopleRequest
                        (id fixture 1147)
                        first
                        (PeopleAct.SetProfile
                            {
                                DisplayName = "Replacement person"
                                Contact = Some "NEW@EXAMPLE.INVALID"
                            })
                ))
                token

        acceptedPeople reprofiled
        let! fresh = find (Some "new@example") (Some Role.CaseReader) None 50

        Expect.equal
            ((success fresh).Items |> List.map _.Principal)
            [ first ]
            "Reprofile derives only the current saved contact"
    }
