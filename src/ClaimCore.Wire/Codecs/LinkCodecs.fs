namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module LinkCodecs =
    open Codec
    open ResponseScalars
    open PresentationKeys

    // Preserve the shared link grammar bound.
    [<Literal>]
    let MaximumLinks = 34

    let link =
        let query task inject extract codec =
            task,
            codec
            |> map inject (fun value ->
                extract value
                |> Option.defaultWith (fun () ->
                    invalidArg "link" "The link input does not match its endpoint."))

        let payloads =
            [
                query
                    QueryTaskId.Context
                    (fun () -> Link.Context)
                    (function
                    | Link.Context -> Some()
                    | _ -> None)
                    RequestCodecs.empty
                query
                    QueryTaskId.CaseGet
                    Link.CaseGet
                    (function
                    | Link.CaseGet value -> Some value
                    | _ -> None)
                    RequestCodecs.caseGetRequest
                query
                    QueryTaskId.CaseAssignees
                    Link.CaseAssignees
                    (function
                    | Link.CaseAssignees value -> Some value
                    | _ -> None)
                    RequestCodecs.caseLookupRequest
                query
                    QueryTaskId.CaseParties
                    Link.CaseParties
                    (function
                    | Link.CaseParties value -> Some value
                    | _ -> None)
                    RequestCodecs.caseLookupRequest
                query
                    QueryTaskId.CaseList
                    Link.CaseList
                    (function
                    | Link.CaseList value -> Some value
                    | _ -> None)
                    RequestCodecs.caseListRequest
                query
                    QueryTaskId.CaseSummary
                    Link.CaseSummary
                    (function
                    | Link.CaseSummary value -> Some value
                    | _ -> None)
                    RequestCodecs.caseSummaryRequest
                query
                    QueryTaskId.CaseTimeline
                    Link.CaseTimeline
                    (function
                    | Link.CaseTimeline value -> Some value
                    | _ -> None)
                    RequestCodecs.timelineRequest
                query
                    QueryTaskId.CaseAttachments
                    Link.CaseAttachments
                    (function
                    | Link.CaseAttachments value -> Some value
                    | _ -> None)
                    RequestCodecs.caseGetRequest
                query
                    QueryTaskId.CaseNotes
                    Link.CaseNotes
                    (function
                    | Link.CaseNotes value -> Some value
                    | _ -> None)
                    RequestCodecs.caseNotesRequest
                query
                    QueryTaskId.CasePayments
                    Link.CasePayments
                    (function
                    | Link.CasePayments value -> Some value
                    | _ -> None)
                    RequestCodecs.casePaymentsRequest
                query
                    QueryTaskId.ReminderList
                    Link.ReminderList
                    (function
                    | Link.ReminderList value -> Some value
                    | _ -> None)
                    RequestCodecs.reminderListRequest
                query
                    QueryTaskId.ReminderGet
                    Link.ReminderGet
                    (function
                    | Link.ReminderGet value -> Some value
                    | _ -> None)
                    RequestCodecs.reminderGetRequest
                query
                    QueryTaskId.WorkViews
                    (fun () -> Link.WorkViews)
                    (function
                    | Link.WorkViews -> Some()
                    | _ -> None)
                    RequestCodecs.empty
                query
                    QueryTaskId.OperationGet
                    (function
                    | Some id -> Link.OperationGet { OperationId = id }
                    | None -> Link.OperationLookupForm)
                    (function
                    | Link.OperationGet value -> Some(Some value.OperationId)
                    | Link.OperationLookupForm -> Some None
                    | _ -> None)
                    (record id |> optionalField "operationId" id uuid |> build)
                query
                    QueryTaskId.PeopleList
                    Link.PeopleList
                    (function
                    | Link.PeopleList value -> Some value
                    | _ -> None)
                    RequestCodecs.actorListRequest
                query
                    QueryTaskId.PeopleGrants
                    Link.PeopleGrants
                    (function
                    | Link.PeopleGrants value -> Some value
                    | _ -> None)
                    RequestCodecs.grantsRequest
                query
                    QueryTaskId.PeopleHistory
                    Link.PeopleHistory
                    (function
                    | Link.PeopleHistory value -> Some value
                    | _ -> None)
                    RequestCodecs.peopleHistoryRequest
                query
                    QueryTaskId.PeopleRequests
                    Link.PeopleRequests
                    (function
                    | Link.PeopleRequests value -> Some value
                    | _ -> None)
                    RequestCodecs.accessRequestsRequest
                query
                    QueryTaskId.DisclosureTrail
                    Link.DisclosureTrail
                    (function
                    | Link.DisclosureTrail value -> Some value
                    | _ -> None)
                    RequestCodecs.disclosureTrailRequest
            ]
            |> Map.ofList

        let endpoint = VocabularyCodec.create QueryTaskId.vocabulary

        record (fun endpoint input -> endpoint, input)
        |> field "endpoint" fst endpoint
        |> field "input" snd jsonObject
        |> build
        |> interpret
            (fun path (endpoint, input) ->
                let codec = Map.find endpoint payloads

                codec.Validate (path + ".input") input
                |> Result.bind (fun () -> codec.Read (path + ".input") input))
            (fun value ->
                let endpoint = Link.endpoint value
                endpoint, (Map.find endpoint payloads).Write value)
        |> named "Link"

    let namedLink =
        record (fun relation label link ->
            {
                NamedLink.Relation = relation
                LabelId = label
                Link = link
            })
        |> field "rel" _.Relation (VocabularyCodec.create LinkRelation.vocabulary)
        |> field "labelId" _.LabelId messageId
        |> field "link" _.Link link
        |> build
        |> named "NamedLink"
