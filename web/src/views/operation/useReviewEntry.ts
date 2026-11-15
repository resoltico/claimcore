import { useCallback, useLayoutEffect, useRef } from "react";
import { sameRequest } from "../../operation/operationDelivery";
import type { EffectIdentity } from "../../operation/operationTypes";

type Reading = {
  identity: EffectIdentity;
  entered: boolean;
  position: number;
  node: HTMLDivElement | null;
};
/** Ephemeral presentation state, retained while Settings removes the portal, never a new review. */
export const useReviewEntry = (
  identity: EffectIdentity | null,
  visible: boolean,
  editing: boolean,
) => {
  const current = useRef({ identity, visible, editing });
  const reading = useRef<Reading | null>(null);
  const attached = useRef<Reading | null>(null);
  const body = useRef<HTMLDivElement | null>(null);
  const heading = useRef<HTMLElement | null>(null);
  const authoring = useRef(0);
  const frame = useRef<number | null>(null);
  const detachIntent = useRef<(() => void) | null>(null);
  const cancel = useCallback(() => {
    detachIntent.current?.();
    detachIntent.current = null;
    if (frame.current !== null) {
      cancelAnimationFrame(frame.current);
      frame.current = null;
    }
  }, []);
  const enter = useCallback(() => {
    cancel();
    const latest = current.current;
    const node = body.current;
    if (node === null || !latest.visible || latest.identity === null) {
      return;
    }
    if (reading.current === null || !sameRequest(reading.current.identity, latest.identity)) {
      reading.current = { identity: latest.identity, entered: false, position: 0, node: null };
    }
    const record = reading.current;
    if (record.entered && record.node === node) {
      return;
    }
    // Move the owned body during commit. Deferred heading focus must yield to real input.
    node.scrollTop = record.entered ? record.position : 0;
    const surface = node.closest('[role="dialog"]') ?? node;
    const authoredEntry = () => {
      record.entered = true;
      record.node = node;
      attached.current = record;
      cancel();
    };
    surface.addEventListener("pointerdown", authoredEntry, true);
    surface.addEventListener("keydown", authoredEntry, true);
    detachIntent.current = () => {
      surface.removeEventListener("pointerdown", authoredEntry, true);
      surface.removeEventListener("keydown", authoredEntry, true);
    };
    frame.current = requestAnimationFrame(() => {
      frame.current = null;
      detachIntent.current?.();
      detachIntent.current = null;
      const now = current.current;
      if (
        !now.visible ||
        now.identity === null ||
        !sameRequest(record.identity, now.identity) ||
        body.current !== node ||
        !node.isConnected ||
        node.closest("[hidden],[inert]") !== null
      ) {
        return;
      }
      node.scrollTop = record.entered ? record.position : 0;
      if (!record.entered && heading.current?.isConnected === true) {
        heading.current.focus({ preventScroll: true });
      }
      record.entered = true;
      record.node = node;
      attached.current = record;
    });
  }, [cancel]);
  const scroll = useCallback(() => {
    const node = body.current;
    if (node === null) {
      return;
    }
    if (node.querySelector("form.operation-form-card") !== null) {
      authoring.current = node.scrollTop;
    } else if (attached.current?.node === node) {
      attached.current.position = node.scrollTop;
    }
  }, []);
  const bodyRef = useCallback(
    (node: HTMLDivElement | null) => {
      cancel();
      if (body.current !== null) {
        scroll();
        body.current.removeEventListener("scroll", scroll);
      }
      if (attached.current !== null) {
        attached.current.node = null;
      }
      body.current = node;
      if (node !== null) {
        node.addEventListener("scroll", scroll);
        enter();
      }
    },
    [cancel, enter, scroll],
  );
  const headingRef = useCallback((node: HTMLElement | null) => {
    heading.current = node;
  }, []);
  useLayoutEffect(() => {
    current.current = { identity, visible, editing };
    if (editing && body.current !== null && attached.current !== null) {
      cancel();
      body.current.scrollTop = authoring.current;
      attached.current = null;
    } else {
      enter();
    }
    return cancel;
  }, [identity, visible, editing, enter, cancel]);
  return { bodyRef, headingRef };
};
