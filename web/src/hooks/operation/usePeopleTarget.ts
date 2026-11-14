import { peoplePrimaryAction } from "../../operation/peopleActionPriority";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { taskDefinitions, type Principal, type OperationBinding } from "@contracts/client";
import type { PersonDocument } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";

import { useCredential, useSessionContext } from "../../session/SessionContext";
import { useContentRoute } from "../SettingsDetourContext";
import type { OperationModel } from "./useOperation";

type Binding = Extract<OperationBinding, { kind: "PEOPLE" }>;
type Snapshot = { key: string; document: PersonDocument; offered: ReadonlyArray<string> };
type Intent = {
  epoch: string;
  source: Snapshot;
  kind: string;
  values: FormValues;
  binding: Binding;
};
const boundActor = (model: OperationModel) =>
  "actorId" in model.bindingSource ? model.bindingSource.actorId : undefined;
const principalValues = (principal: Principal): FormValues => ({
  "input:principal.kind": principal.kind,
  "input:principal.issuer": principal.issuer,
  "input:principal.subject": principal.subject,
});
const retained = (model: OperationModel) =>
  model.phase !== "ACCEPTED" && (model.dirty || model.prepared !== null || model.priorUncertain);
const actorValues = (
  document: PersonDocument,
  kind: string,
  values: FormValues = {},
): FormValues => ({
  ...values,
  "input:principal.kind": document.context.principal.kind,
  ...(kind === "people.setProfile" && document.context.profileSeed !== null
    ? {
        "input:profile.displayName": document.context.profileSeed.displayName,
        ...(typeof document.context.profileSeed.contact === "string"
          ? { "input:profile.contact": document.context.profileSeed.contact }
          : {}),
      }
    : {}),
});
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
        document.context.actorId !== actorId
      ) {
        return;
      }
      const offered = document.context.tasks.flatMap((id) => {
        const task = taskDefinitions.find((candidate) => candidate.id === id);
        return task?.kind === "OPERATION" && task.subject === "PEOPLE" ? [task.act] : [];
      });
      setSnapshot((prior) =>
        prior?.key === key && prior.document === document ? prior : { key, document, offered },
      );
    },
    [key, actorId],
  );
  return { source, epoch, actorId, observe };
};
const useActorDefault = (model: OperationModel, source: Snapshot | null, fixed: boolean) => {
  const seeded = useRef<string | null>(null);
  useEffect(() => {
    if (retained(model) || model.busy || fixed) {
      return;
    }
    if (source === null) {
      if (seeded.current !== null && model.phase !== "ACCEPTED") {
        seeded.current = null;
        model.discard(false, "people.admit", {}, { kind: "PEOPLE" });
      }
      return;
    }
    const kind = peoplePrimaryAction(source.offered);
    if (kind !== undefined) {
      seeded.current = source.key;
      model.discard(false, kind, actorValues(source.document, kind), {
        kind: "PEOPLE",
        actorId: source.document.context.actorId,
      });
    }
  }, [model, source, fixed]);
};
const admissionBinding = (model: OperationModel, principal: Principal | null) =>
  principal !== null &&
  model.entry.kind === "people.admit" &&
  model.values["input:principal.kind"] === principal.kind &&
  model.values["input:principal.issuer"] === principal.issuer &&
  model.values["input:principal.subject"] === principal.subject;
const personBinding = (model: OperationModel, source: Snapshot | null): source is Snapshot =>
  source !== null &&
  boundActor(model) === source.document.context.actorId &&
  model.entry.kind !== "people.admit" &&
  model.entry.kind !== "people.register";
const defaultPeopleActKind = (source: Snapshot | null) =>
  source === null ? "people.admit" : (peoplePrimaryAction(source.offered) ?? "people.admit");
const matchingIntent = (intent: Intent | null, kind: string, epoch: string, available: boolean) =>
  intent?.kind === kind && intent.epoch === epoch && available ? intent : null;
const discardBinding = (
  kind: string,
  principal: Principal | null,
  source: Snapshot | null,
): Binding => {
  if (principal !== null) {
    return { kind: "PEOPLE" };
  }
  if (source === null || kind === "people.admit" || kind === "people.register") {
    return { kind: "PEOPLE" };
  }
  return { kind: "PEOPLE", actorId: source.document.context.actorId };
};
const discardSeeds = (
  kind: string,
  principal: Principal | null,
  source: Snapshot | null,
  prefill: FormValues = {},
): FormValues => {
  if (principal !== null) {
    return principalValues(principal);
  }
  if (source !== null) {
    return actorValues(source.document, kind, prefill);
  }
  return {};
};
const targetMode = (
  actorId: string | null,
  fixed: boolean,
  retainedDraft: boolean,
): "list" | "bound" | "retained" | "unbound" => {
  if (actorId === null) {
    return "list";
  }
  if (fixed) {
    return "bound";
  }
  return retainedDraft ? "retained" : "unbound";
};
export const usePeopleTarget = (
  model: OperationModel,
  revision: number,
  available: boolean,
  admissionPrincipal: Principal | null = null,
) => {
  const { source, epoch, actorId, observe } = useActorDisclosure(revision, available);
  const [intent, setIntent] = useState<Intent | null>(null);
  const admissionBound = admissionBinding(model, admissionPrincipal);
  const actorBound = personBinding(model, source);
  const fixed = admissionBound || actorBound;
  useActorDefault(model, source, fixed);
  const begin = (
    kind: string,
    values: FormValues = {},
    grant?: Pick<PersonDocument["context"]["revocations"][number], "role" | "scope">,
  ) => {
    if (source === null || !source.offered.includes(kind) || model.busy) {
      return;
    }
    const binding: Binding = {
      kind: "PEOPLE",
      actorId: source.document.context.actorId,
    };
    const seeded =
      grant === undefined
        ? values
        : {
            ...values,
            "input:role": grant.role,
            "input:scope": grant.scope.kind,
            ...(grant.scope.kind === "CASE" ? { "input:caseId": grant.scope.caseId } : {}),
          };
    if (retained(model)) {
      setIntent({ epoch, source, kind, values: seeded, binding });
      model.requestDiscard(kind);
    } else {
      model.discard(false, kind, actorValues(source.document, kind, seeded), binding);
    }
  };
  const discard: OperationModel["discard"] = (acknowledged, kind, values, explicitBinding) => {
    const nextKind = kind ?? defaultPeopleActKind(source);
    const pending = matchingIntent(intent, nextKind, epoch, available);
    const selected = pending?.source ?? source;
    const binding =
      explicitBinding ??
      pending?.binding ??
      discardBinding(nextKind, admissionBound ? admissionPrincipal : null, selected);
    model.discard(
      acknowledged ?? false,
      nextKind,
      values ??
        discardSeeds(
          nextKind,
          admissionBound ? admissionPrincipal : null,
          selected,
          pending?.values,
        ),
      binding,
    );
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
    offered: source?.offered ?? [],
    primary: peoplePrimaryAction(source?.offered ?? []),
    person: actorBound ? source.document : null,
    mode: targetMode(actorId, fixed, retained(model)),
  };
};
export type PeopleTarget = ReturnType<typeof usePeopleTarget>;
