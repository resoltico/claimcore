namespace ClaimCore.Domain.Changes

module NoteDescription =
    /// The caller reads the preceding accepted text only for changed notes, in the action snapshot.
    let describe (before: NoteValue option) (after: NoteValue) =
        {
            NotePartDescription.Text = Difference.value (before |> Option.bind _.Text) after.Text
            Removed = Difference.value (before |> Option.map _.Removed) (Some after.Removed)
        }
