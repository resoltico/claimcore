import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ActorSummary, PrincipalInput } from "@contracts/client";
import type { FormValues } from "../../protocol/forms";
import { useCredential } from "../../session/SessionContext";
import { useRoute } from "../../routing/useRoute";
import type { OperationModel } from "./useOperation";
type Snapshot = { key: string; actor: ActorSummary; offered: ReadonlyArray<string> };
type Intent = { epoch: string; actor: ActorSummary; kind: string };
const targetValues = (principal: PrincipalInput): FormValues => ({
  "target.kind": principal.kind,
  "target.issuer": principal.issuer,
  "target.subject": principal.subject,
});
const matches = (values: FormValues, principal: PrincipalInput) =>
  values["target.kind"] === principal.kind &&
  values["target.issuer"] === principal.issuer &&
  values["target.subject"] === principal.subject;
const retained = (model: OperationModel) =>
  model.phase !== "ACCEPTED" && (model.dirty || model.prepared !== null || model.priorUncertain);
const defaultKind = (offered: ReadonlyArray<string>) =>
  offered.includes("GRANT") ? "GRANT" : offered[0];
const sameSnapshot = (left: Snapshot | null, right: Snapshot) =>
  left !== null &&
  left.key === right.key &&
  left.actor.actorId === right.actor.actorId &&
  left.actor.enabled === right.actor.enabled &&
  left.actor.principal.kind === right.actor.principal.kind &&
  left.actor.principal.issuer === right.actor.principal.issuer &&
  left.actor.principal.subject === right.actor.principal.subject &&
  left.offered.join(",") === right.offered.join(",");
const useActorDisclosure = (revision: number, available: boolean) => {
  const credential = useCredential();
  const route = useRoute();
  const actorId = route.screen === "authority" && route.actorId !== "" ? route.actorId : null;
  const epoch = `${credential}:${revision}`;
  const key = `${epoch}:${actorId ?? ""}`;
  const current = useRef({ key, available });
  useLayoutEffect(() => {
    current.current = { key, available };
  }, [key, available]);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  if (snapshot !== null && (!available || snapshot.key !== key)) {
    setSnapshot(null);
  }
  const source = available && snapshot?.key === key ? snapshot : null;
  const observe = useCallback(
    (actor: ActorSummary, offered: ReadonlyArray<string>) => {
      if (current.current.key !== key || !current.current.available || actor.actorId !== actorId) {
        return;
      }
      const next = { key, actor, offered: offered.filter((kind) => kind !== "REGISTER") };
      setSnapshot((prior) => (sameSnapshot(prior, next) ? prior : next));
    },
    [key, actorId],
  );
  return { source, epoch, actorId, observe };
};
const useActorDefault = (model: OperationModel, source: Snapshot | null, fixed: boolean) => {
  const seeded = useRef<string | null>(null);
  useEffect(() => {
    if (retained(model) || model.busy) {
      return;
    }
    const id = model.values["operationId"];
    if (fixed) {
      if (typeof id === "string") {
        seeded.current = id;
      }
      return;
    }
    if (source === null) {
      if (model.phase !== "ACCEPTED" && seeded.current === id) {
        seeded.current = null;
        model.discard(false, "REGISTER");
      }
      return;
    }
    const kind = defaultKind(source.offered);
    if (kind !== undefined) {
      model.discard(false, kind, targetValues(source.actor.principal));
    }
  }, [source, fixed, model]);
};
const resetPrincipal = (
  intent: Intent | null,
  kind: string,
  epoch: string,
  available: boolean,
  fallback: PrincipalInput | undefined,
) =>
  intent?.kind === kind && intent.epoch === epoch && available ? intent.actor.principal : fallback;
const useActorConsent = (
  model: OperationModel,
  source: Snapshot | null,
  epoch: string,
  available: boolean,
  fixed: boolean,
) => {
  const [intent, setIntent] = useState<Intent | null>(null);
  const begin = (kind: string) => {
    if (source === null || !source.offered.includes(kind) || model.busy) {
      return;
    }
    if (retained(model)) {
      setIntent({ epoch, actor: source.actor, kind });
      model.requestDiscard(kind);
    } else {
      model.discard(false, kind, targetValues(source.actor.principal));
    }
  };
  const discard = (
    acknowledged = false,
    kind = source === null ? "REGISTER" : (defaultKind(source.offered) ?? "REGISTER"),
  ) => {
    const fallback = fixed && source !== null ? source.actor.principal : undefined;
    const principal = resetPrincipal(intent, kind, epoch, available, fallback);
    model.discard(
      acknowledged,
      kind,
      principal === undefined ? undefined : targetValues(principal),
    );
    setIntent(null);
  };
  const requestDiscard = (kind: string | null) => {
    setIntent(null);
    model.requestDiscard(kind);
  };
  return { begin, model: { ...model, discard, requestDiscard } };
};
const targetMode = (model: OperationModel, actorId: string | null, fixed: boolean) => {
  if (actorId === null) {
    return "list";
  }
  if (fixed) {
    return "bound";
  }
  if (retained(model)) {
    return "retained";
  }
  return "unbound";
};
export const useAuthorityTarget = (model: OperationModel, revision: number, available: boolean) => {
  const { source, epoch, actorId, observe } = useActorDisclosure(revision, available);
  const fixed =
    source !== null &&
    matches(model.values, source.actor.principal) &&
    model.entry.kind !== "REGISTER";
  useActorDefault(model, source, fixed);
  const consent = useActorConsent(model, source, epoch, available, fixed);
  return {
    ...consent,
    observe,
    offered: source?.offered ?? [],
    actor: source?.actor ?? null,
    mode: targetMode(model, actorId, fixed),
  };
};
export type AuthorityTarget = ReturnType<typeof useAuthorityTarget>;
