import { useContext, useLayoutEffect, useRef, type ContextType } from "react";
import { DateFieldStateContext } from "react-aria-components/DateField";
import { DateEntryDraftContext } from "./DateEntryDraftContext";
import { sameDateEntryDraft, type DateEntryPart } from "../operation/dateEntryDraft";
type State = NonNullable<ContextType<typeof DateFieldStateContext>>;
const partOrder = { year: 0, month: 1, day: 2 };
const parts = (state: State): ReadonlyArray<DateEntryPart> =>
  state.segments
    .flatMap((segment) =>
      (segment.type === "year" || segment.type === "month" || segment.type === "day") &&
      !segment.isPlaceholder &&
      typeof segment.value === "number"
        ? [{ type: segment.type, value: segment.value }]
        : [],
    )
    .sort((left, right) => partOrder[left.type] - partOrder[right.type]);
const partial = (state: State) =>
  state.segments.some(
    (segment) =>
      (segment.type === "year" || segment.type === "month" || segment.type === "day") &&
      segment.isPlaceholder,
  );
const restorePart = (state: State, desired: ReadonlyArray<DateEntryPart>) => {
  const current = parts(state);
  const extra = current.find((part) => !desired.some((entry) => entry.type === part.type));
  if (extra !== undefined) {
    state.clearSegment(extra.type);
    return true;
  }
  const missing = desired.find(
    (part) => !current.some((entry) => entry.type === part.type && entry.value === part.value),
  );
  if (missing !== undefined) {
    state.setSegment(missing.type, missing.value);
    return true;
  }
  return false;
};
const capture = (
  state: State,
  drafts: NonNullable<ContextType<typeof DateEntryDraftContext>>,
  path: string,
  canonical: string,
) => {
  const captured = partial(state) ? parts(state) : [];
  const snapshot = captured.length === 0 ? null : { canonical: "", parts: captured };
  if (!sameDateEntryDraft(drafts.entries[path], snapshot)) {
    drafts.capture(path, canonical, snapshot?.parts ?? null);
  }
};
/** Keep only public typed segment values; restoration never creates a complete placeholder date. */
export const DateEntryDraft = ({
  path,
  canonical,
  disabled,
}: {
  path: string;
  canonical: string;
  disabled: boolean;
}) => {
  const state = useContext(DateFieldStateContext);
  const drafts = useContext(DateEntryDraftContext);
  const previous = useRef(canonical);
  const restoring = useRef(drafts?.entries[path] ?? null);
  useLayoutEffect(() => {
    if (state === null || drafts === null || disabled) {
      return;
    }
    if (previous.current !== canonical) {
      previous.current = canonical;
      const saved = drafts.entries[path];
      restoring.current = saved?.canonical === canonical ? saved : null;
    }
    const pending = restoring.current;
    if (pending !== null) {
      if (restorePart(state, pending.parts)) {
        return;
      }
      restoring.current = null;
    }
    capture(state, drafts, path, canonical);
  }, [state, drafts, path, canonical, disabled]);
  return null;
};
