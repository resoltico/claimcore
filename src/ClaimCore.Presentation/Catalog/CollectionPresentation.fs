namespace ClaimCore.Presentation

open ClaimCore.Domain

module CollectionPresentation =
    let private prefix collection =
        "collection." + TaskCollectionId.key collection

    let labelId collection = prefix collection + ".label"
    let itemLabelId collection = prefix collection + ".item"
    let addLabelId collection = prefix collection + ".add"
    let removeLabelId collection = prefix collection + ".remove"
