import { useRecovery } from "../../recovery/RecoveryContext";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  useMemo,
  type ReactNode,
} from "react";
import type { OwnerScope } from "../../protocol/ownerScope";
import { sameOwnerScope } from "../../protocol/ownerScope";
import { useSessionContext } from "../../session/SessionContext";
import { setNavigationBlocker, cancelPendingNavigation } from "../../routing/navigation";
import { parseRoute } from "../../routing/routes";
import { Dialog } from "../../ui/Dialog";
import { usePresentation } from "../../presentation/context";
type Owner = OwnerScope & { credential: string };
export type NoteIntent = Readonly<{
  owner: Owner;
  caseId: string;
  generation: number;
  id: string;
  text: string;
}>;
type Draft = {
  owner: Owner;
  caseId: string;
  reference: string | null;
  text: string;
  generation: number;
  intent: string | null;
  operationId: string | null;
};
const same = (a: Owner, b: Owner) => sameOwnerScope(a, b) && a.credential === b.credential;
type State = {
  draft: Draft | null;
  observe: (id: string, reference: string | null) => void;
  edit: (id: string, text: string) => void;
  capture: (id: string) => NoteIntent | null;
  accepted: (intent: NoteIntent) => void;
  discard: (caseId: string, generation: number) => void;
  bind: (intent: NoteIntent, operationId: string) => void;
  reconciled: (operationId: string, caseId: string) => void;
};
const Context = createContext<State | null>(null);
const belongs = (draft: Draft | null, owner: Owner | null): draft is Draft =>
  draft !== null && owner !== null && same(draft.owner, owner);
const matchingIntent = (draft: Draft, intent: NoteIntent) =>
  same(draft.owner, intent.owner) &&
  draft.caseId === intent.caseId &&
  draft.generation === intent.generation &&
  draft.intent === intent.id;
const sameCase = (a: Draft | null, b: Draft | null) =>
  a !== null && b !== null && same(a.owner, b.owner) && a.caseId === b.caseId;
const authenticatedOwner = (owner: Owner, session: ReturnType<typeof useSessionContext>["state"]) =>
  session.kind === "authenticated" &&
  owner.credential === session.token &&
  owner.sessionEpoch === session.epoch;
const useOwnedNoteDraft = (owner: Owner | null): { state: State; retained: Draft | null } => {
  const { state: session } = useSessionContext();
  const [stored, setStored] = useState<Draft | null>(null);
  const generation = useRef(0);
  const valid =
    stored === null ||
    (authenticatedOwner(stored.owner, session) && (owner === null || same(stored.owner, owner)));
  const draft = valid ? stored : null;
  if (!valid) {
    setStored(null);
  }
  const observe = useCallback(
    (caseId: string, reference: string | null) => {
      if (owner === null) {
        return;
      }
      setStored((current) => {
        if (belongs(current, owner) && current.caseId === caseId) {
          return reference !== null && reference !== current.reference
            ? { ...current, reference }
            : current;
        }
        return {
          owner,
          caseId,
          reference,
          generation: ++generation.current,
          text: "",
          intent: null,
          operationId: null,
        };
      });
    },
    [owner],
  );
  const edit = useCallback(
    (caseId: string, text: string) => {
      setStored((current) =>
        belongs(current, owner) && current.caseId === caseId
          ? { ...current, text, generation: ++generation.current, intent: null, operationId: null }
          : current,
      );
    },
    [owner],
  );
  const capture = useCallback(
    (caseId: string): NoteIntent | null => {
      if (!belongs(draft, owner) || draft.caseId !== caseId || draft.text.length === 0) {
        return null;
      }
      const id = crypto.randomUUID();
      setStored((current) =>
        current === draft ? { ...current, intent: id, operationId: null } : current,
      );
      return { owner: draft.owner, caseId, generation: draft.generation, id, text: draft.text };
    },
    [draft, owner],
  );
  const clear = useCallback(
    (matches: (current: Draft) => boolean) => {
      setStored((current) =>
        belongs(current, owner) && matches(current)
          ? {
              ...current,
              text: "",
              intent: null,
              operationId: null,
              generation: ++generation.current,
            }
          : current,
      );
    },
    [owner],
  );
  const discard = useCallback(
    (caseId: string, version: number) => {
      clear((current) => current.caseId === caseId && current.generation === version);
    },
    [clear],
  );
  const accepted = useCallback(
    (intent: NoteIntent) => {
      clear((current) => matchingIntent(current, intent));
    },
    [clear],
  );
  const bind = useCallback(
    (intent: NoteIntent, operationId: string) => {
      setStored((current) =>
        belongs(current, owner) &&
        matchingIntent(current, intent) &&
        current.operationId !== operationId
          ? { ...current, operationId }
          : current,
      );
    },
    [owner],
  );
  const reconciled = useCallback(
    (operationId: string, caseId: string) => {
      clear((current) => current.operationId === operationId && current.caseId === caseId);
    },
    [clear],
  );
  const state = useMemo(
    () => ({
      draft: belongs(draft, owner) ? draft : null,
      observe,
      edit,
      capture,
      accepted,
      discard,
      bind,
      reconciled,
    }),
    [draft, owner, observe, edit, capture, accepted, discard, bind, reconciled],
  );
  return { state, retained: draft };
};
const permitsRoute = (draft: Draft, destination: string) => {
  const route = parseRoute(new URL(destination, location.origin));
  if (route.screen === "settings") {
    return true;
  }
  if (route.screen !== "case" && route.screen !== "caseAct") {
    return false;
  }
  return route.target.kind === "ID"
    ? route.target.caseId === draft.caseId
    : route.target.caseReference === draft.reference;
};
const NoteDeparture = ({ state, draft }: { state: State; draft: Draft | null }) => {
  const p = usePresentation();
  const [pending, setPending] = useState<{ draft: Draft; proceed: () => void } | null>(null);
  const visible = state.draft !== null && pending !== null && sameCase(pending.draft, draft);
  // Clear a stale decision during render; Dashboard's owner-scoped navigation cancels its continuation.
  if (pending !== null && draft !== null && !sameCase(pending.draft, draft)) {
    setPending(null);
  }
  useEffect(() => {
    if (draft === null || draft.text.length === 0) {
      return;
    }
    return setNavigationBlocker((proceed, destination) => {
      if (destination !== undefined && permitsRoute(draft, destination)) {
        return true;
      }
      setPending({ draft, proceed });
      return false;
    }, 0);
  }, [draft]);
  const discard = () => {
    setPending(null);
    if (
      pending !== null &&
      draft !== null &&
      sameCase(pending.draft, draft) &&
      pending.draft.generation === draft.generation
    ) {
      // Explicit discard is scoped to the captured draft, just like its own acceptance.
      state.discard(draft.caseId, draft.generation);
      pending.proceed();
    } else {
      cancelPendingNavigation();
    }
  };
  return (
    <Dialog
      title={p.text("ui.changeActTitle")}
      closeLabel={p.text("ui.keepEditing")}
      description={p.text("ui.leaveDraftDescription")}
      isOpen={visible}
      isDismissable
      onOpenChange={() => {
        setPending(null);
        cancelPendingNavigation();
      }}
    >
      <button type="button" onClick={discard}>
        {p.text("ui.discardAndLeave")}
      </button>
    </Dialog>
  );
};
export const InlineNoteDraftProvider = ({
  owner,
  children,
}: {
  owner: Owner | null;
  children: ReactNode;
}) => {
  const { state, retained } = useOwnedNoteDraft(owner);
  return (
    <Context value={state}>
      {children}
      <NoteDeparture state={state} draft={retained} />
    </Context>
  );
};
export const useInlineNoteDraft = () => {
  const state = useContext(Context);
  if (state === null) {
    throw new Error("Inline notes require a verified workspace draft owner.");
  }
  return state;
};

export const InlineNoteRecovery = () => {
  const recovery = useRecovery();
  const { reconciled } = useInlineNoteDraft();
  useEffect(() => {
    for (const final of Object.values(recovery.finals)) {
      if (
        final.record.act === "note.add" &&
        final.outcome.kind === "RECORDED" &&
        final.outcome.caseId !== null
      ) {
        reconciled(final.record.operationId, final.outcome.caseId);
      }
    }
  }, [recovery.finals, reconciled]);
  return null;
};
