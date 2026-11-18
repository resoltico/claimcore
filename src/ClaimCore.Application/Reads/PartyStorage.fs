namespace ClaimCore.Application.Reads

open System
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal PartyStorage =
    let columns =
        "party_id,case_id,revision,created_sequence,created_effect_index,removed,role,name,details"

    let row value : StoredParty =
        let corrupt () =
            raise (RecordCorruptException "The projected party is invalid.")

        let parse f raw =
            f raw |> Result.defaultWith (fun _ -> corrupt ())

        let name =
            DatabaseSession.namedOptional<string> "name" value
            |> Option.map (parse PartyName.parse)

        let details =
            DatabaseSession.namedOptional<string> "details" value
            |> Option.map (parse PartyDetails.parse)

        let content =
            match DatabaseSession.namedOptional<string> "role" value with
            | Some raw ->
                let role =
                    Vocabulary.tryParse PartyRole.vocabulary raw |> Option.defaultWith corrupt

                PartyContent.create role name details
                |> Result.defaultWith (fun _ -> corrupt ())
                |> Some
            | None when name.IsNone && details.IsNone -> None
            | None -> corrupt ()

        {
            PartyId =
                DatabaseSession.namedValue<Guid> "party_id" value
                |> DatabaseSession.admitted PartyId.create
            CaseId =
                DatabaseSession.namedValue<Guid> "case_id" value
                |> DatabaseSession.admitted CaseId.create
            Revision =
                DatabaseSession.namedValue<int64> "revision" value
                |> DatabaseSession.admitted Revision.create
            CreatedPosition =
                let sequence =
                    DatabaseSession.namedValue<int64> "created_sequence" value
                    |> DatabaseSession.admitted Sequence.create

                EffectPosition.create
                    sequence
                    (DatabaseSession.namedValue<int> "created_effect_index" value)
                |> Option.defaultWith corrupt
            Value =
                {
                    Removed = DatabaseSession.namedValue<bool> "removed" value
                    Content = content
                }
        }

    let current connection ids ct =
        task {
            if List.isEmpty ids then
                return Map.empty
            else
                let! rows =
                    DatabaseSession.namedRows
                        connection
                        ("SELECT "
                         + columns
                         + " FROM claimcore.case_parties WHERE case_id=ANY($1::uuid[]) AND NOT removed AND role IS NOT NULL ORDER BY case_id,created_sequence,created_effect_index")
                        [| box (ids |> List.map CaseId.value |> List.toArray) |]
                        ct

                return
                    rows
                    |> List.map row
                    |> List.groupBy _.CaseId
                    |> List.map (fun (id, rows) -> id, rows |> List.choose _.Value.Content)
                    |> Map.ofList
        }
