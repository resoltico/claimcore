namespace ClaimCore.Web

open ClaimCore.Application
open HttpInputSupport

module HttpRecoveryInput =
    let page maximum root =
        let values = properties root |> exactProperties [ "cursor"; "limit"; "view" ]
        let limit = required "limit" values |> integerValue

        if limit < 1 || limit > maximum then
            fail "The requested page size is outside the supported range."

        let view =
            match optionalString "view" values with
            | None -> RecoveryListView.Pending
            | Some "PENDING" -> RecoveryListView.Pending
            | Some "TERMINAL" -> RecoveryListView.Terminal
            | Some _ -> fail "Use PENDING or TERMINAL recovery view."

        {
            View = view
            Cursor = optionalString "cursor" values
            Limit = limit
        }

    let inspect maximum root =
        let values =
            properties root
            |> exactProperties [ "operationId"; "attemptCursor"; "attemptLimit" ]

        let limit = required "attemptLimit" values |> integerValue

        if limit < 1 || limit > maximum then
            fail "The requested page size is outside the supported range."

        {
            OperationId = required "operationId" values |> stringValue |> operationIdValue
            AttemptCursor = optionalString "attemptCursor" values
            AttemptLimit = limit
        }
