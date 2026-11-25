import { principalCreationTask } from "../../operation/metadata";
import { grantFields } from "../../operation/grantTargetDraft";
import { useEndpointCache } from "../EndpointProvider";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { Principal } from "@contracts/client";
import type { PersonDocument } from "../../protocol/types";

import {
  allActions,
  documentActions,
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
const retained = (model: OperationModel) => model.protectsIntent;
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
  return { source, epoch, actorId, observe, selectionCurrent: actorId === null || source !== null };
};
const admissionIsBound = (model: OperationModel, principal: Principal | null) =>
  principal !== null &&
  model.task.creates &&
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
const matchesFixedInputs = (
  selected: OperationAffordance | undefined,
  candidate: OperationAffordance,
  values: Parameters<typeof grantFields>[0],
) => {
  const authored = grantFields(values);
  const seeds = initialAffordanceValues(candidate);
  const editable = new Set<string>(candidate.editable);
  const fixed =
    selected === undefined
      ? []
      : Object.keys(initialAffordanceValues(selected)).filter(
          (key) => !new Set<string>(selected.editable).has(key),
        );
  return (
    fixed.every(
      (key) => !editable.has(key) && Object.hasOwn(seeds, key) && seeds[key] === authored[key],
    ) && Object.entries(seeds).every(([key, value]) => editable.has(key) || authored[key] === value)
  );
};
const sameFreshTarget = (
  model: OperationModel,
  request: { source: Snapshot; task: string; prepared: OperationModel["prepared"] },
) =>
  !model.priorUncertain &&
  boundActor(model) === request.source.document.subject.actorId &&
  model.entry.kind === request.task &&
  model.prepared === request.prepared;
export const usePeopleTarget = (
  model: OperationModel,
  revision: number,
  available: boolean,
  admissionPrincipal: Principal | null = null,
) => {
  const { source, epoch, actorId, observe, selectionCurrent } = useActorDisclosure(
    revision,
    available,
  );
  const cache = useEndpointCache();
  const freshReview = useRef<{
    epoch: string;
    source: Snapshot;
    task: string;
    prepared: OperationModel["prepared"];
  } | null>(null);
  useEffect(() => {
    const request = freshReview.current;
    if (request === null) {
      return;
    }
    if (request.epoch !== epoch || actorId !== request.source.document.subject.actorId) {
      freshReview.current = null;
      return;
    }
    if (!available) {
      return;
    }
    if (!sameFreshTarget(model, request)) {
      freshReview.current = null;
      return;
    }
    if (!available || source === null || source.document === request.source.document) {
      return;
    }
    freshReview.current = null;
    const action = source.actions.find(
      (candidate) =>
        candidate.task === model.entry.kind &&
        candidate.blocked === null &&
        matchesFixedInputs(model.affordance, candidate, model.values),
    );
    if (action !== undefined) {
      model.discard(false, action.task, model.values, action.binding, action);
    }
  }, [available, epoch, actorId, model, source]);
  const [intent, setIntent] = useState<Intent | null>(null);
  const admissionBound = admissionIsBound(model, admissionPrincipal);
  const actorBound =
    source !== null && boundActor(model) === source.document.subject.actorId && !model.task.creates;
  const fixed = admissionBound || actorBound;
  const seeded = useRef<string | null>(null);
  const recommended =
    source === null
      ? undefined
      : operationActions(
          source.document.screen.regions
            .filter((region) => region.role === "RECOMMENDATION")
            .flatMap((region) => region.actions ?? []),
        ).find((action) => action.blocked === null);
  useEffect(() => {
    if (retained(model) || model.busy || fixed) {
      return;
    }
    if (source !== null && recommended !== undefined) {
      seeded.current = source.key;
      adopt(model, recommended);
    } else if (source === null && seeded.current !== null && model.phase !== "ACCEPTED") {
      seeded.current = null;
      if (boundActor(model) !== undefined) {
        model.discard(false, principalCreationTask().id, {});
      }
    }
  }, [model, source, recommended, fixed]);
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
      source?.actions.find((candidate) => candidate.task === (kind ?? recommended?.task));
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
    model: {
      ...model,
      discard,
      requestDiscard,
      refreshPrecondition: () => {
        if (source !== null && actorBound && available && !model.busy && !model.priorUncertain) {
          freshReview.current = { epoch, source, task: model.entry.kind, prepared: model.prepared };
          cache?.invalidate();
        }
      },
    },
    begin,
    admissionBound,
    selectionCurrent,
    observe,
    offered: source?.actions.map((action) => action.task) ?? [],
    actions: source === null ? [] : documentActions(source.document),
    person: actorBound ? source.document : null,
    mode: targetMode(actorId, fixed, model),
  };
};
export type PeopleTarget = ReturnType<typeof usePeopleTarget>;
