import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { Principal } from "@contracts/client";
import type { PersonDocument } from "../../protocol/types";

import {
  allActions,
  initialAffordanceValues,
  operationActions,
  type OperationAffordance,
} from "../../operation/affordances";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { useContentRoute } from "../SettingsDetourContext";
import type { OperationModel } from "./useOperation";

type Snapshot = {
  key: string;
  document: PersonDocument;
  actions: ReadonlyArray<OperationAffordance>;
};
type Intent = { epoch: string; source: Snapshot; action: OperationAffordance };
const retained = (model: OperationModel) =>
  model.phase !== "ACCEPTED" && (model.dirty || model.prepared !== null || model.priorUncertain);
const boundActor = (model: OperationModel) =>
  "actorId" in model.bindingSource ? model.bindingSource.actorId : undefined;
const useActorDisclosure = (revision: number, available: boolean) => {
  const credential = useCredential();
  const { epoch: sessionEpoch } = useSessionContext().state;
  const route = useContentRoute();
  const actorId = route.screen === "people" && route.actorId !== "" ? route.actorId : null;
  const epoch = `${sessionEpoch}:${credential}:${revision}`;
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
    (document: PersonDocument) => {
      if (
        current.current.key !== key ||
        !current.current.available ||
        document.subject.actorId !== actorId
      ) {
        return;
      }
      const actions = operationActions(allActions(document));
      setSnapshot((prior) =>
        prior?.key === key && prior.document === document ? prior : { key, document, actions },
      );
    },
    [key, actorId],
  );
  return { source, epoch, actorId, observe };
};
const admissionIsBound = (model: OperationModel, principal: Principal | null) =>
  principal !== null &&
  model.entry.kind === "people.admit" &&
  model.values["input:principal.kind"] === principal.kind &&
  model.values["input:principal.issuer"] === principal.issuer &&
  model.values["input:principal.subject"] === principal.subject;
const targetMode = (actorId: string | null, fixed: boolean, model: OperationModel) => {
  if (actorId === null) {
    return "list" as const;
  }
  if (fixed) {
    return "bound" as const;
  }
  return retained(model) ? ("retained" as const) : ("unbound" as const);
};
const discardAction = (
  model: OperationModel,
  action: OperationAffordance,
  acknowledged = false,
  values = initialAffordanceValues(action),
  binding = action.binding,
) => {
  model.discard(acknowledged, action.task, values, binding, action);
};
const adopt = (model: OperationModel, action: OperationAffordance) => {
  model.discard(false, action.task, initialAffordanceValues(action), action.binding, action);
};
export const usePeopleTarget = (
  model: OperationModel,
  revision: number,
  available: boolean,
  admissionPrincipal: Principal | null = null,
) => {
  const { source, epoch, actorId, observe } = useActorDisclosure(revision, available);
  const [intent, setIntent] = useState<Intent | null>(null);
  const admissionBound = admissionIsBound(model, admissionPrincipal);
  const actorBound =
    source !== null &&
    boundActor(model) === source.document.subject.actorId &&
    model.entry.kind !== "people.admit" &&
    model.entry.kind !== "people.register";
  const fixed = admissionBound || actorBound;
  const seeded = useRef<string | null>(null);
  const primary = source?.document.actions.find(
    (action) => action.primary && action.blocked === null,
  );
  useEffect(() => {
    if (retained(model) || model.busy || fixed) {
      return;
    }
    if (source !== null && primary !== undefined && primary.task !== "people.requests.dismiss") {
      seeded.current = source.key;
      adopt(model, primary);
    } else if (source === null && seeded.current !== null && model.phase !== "ACCEPTED") {
      seeded.current = null;
      if (boundActor(model) !== undefined) {
        model.discard(false, "people.admit", {}, { kind: "PEOPLE" });
      }
    }
  }, [model, source, primary, fixed]);
  const begin = (action: OperationAffordance) => {
    if (source === null || !source.actions.includes(action) || model.busy) {
      return;
    }
    if (retained(model)) {
      setIntent({ epoch, source, action });
      model.requestDiscard(action.task);
    } else {
      adopt(model, action);
    }
  };
  const discard: OperationModel["discard"] = (acknowledged, kind, values, binding, explicit) => {
    const pending =
      intent?.epoch === epoch && available && intent.action.task === kind ? intent : null;
    const action =
      explicit ??
      pending?.action ??
      source?.actions.find((candidate) => candidate.task === (kind ?? primary?.task));
    if (action === undefined) {
      model.discard(acknowledged, kind, values, binding);
    } else {
      discardAction(model, action, acknowledged, values, binding);
    }
    setIntent(null);
  };
  const requestDiscard = (kind: string | null) => {
    setIntent(null);
    model.requestDiscard(kind);
  };
  return {
    model: { ...model, discard, requestDiscard },
    begin,
    admissionBound,
    observe,
    offered: source?.actions.map((action) => action.task) ?? [],
    actions: source === null ? [] : source.document.actions,
    primary: primary?.task,
    person: actorBound ? source.document : null,
    mode: targetMode(actorId, fixed, model),
  };
};
export type PeopleTarget = ReturnType<typeof usePeopleTarget>;
