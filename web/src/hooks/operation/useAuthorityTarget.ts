import { authorityPrimaryAction } from "../../operation/authorityActionPriority";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { taskDefinitions, type PrincipalInput, type AuthorityGrantInput } from "@contracts/client";
import type { PersonDocument } from "../../protocol/types";
import type { FormValues } from "../../protocol/forms";
import type { TaskBindingSource } from "../../operation/taskForm";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { useContentRoute } from "../SettingsDetourContext";
import type { OperationModel } from "./useOperation";

type Binding = Extract<TaskBindingSource, { kind: "AUTHORITY" }>;
type Snapshot = { key: string; document: PersonDocument; offered: ReadonlyArray<string> };
type Intent = {
  epoch: string;
  source: Snapshot;
  kind: string;
  values: FormValues;
  binding: Binding;
};
const samePrincipal = (left: PrincipalInput | undefined, right: PrincipalInput) =>
  left?.kind === right.kind && left.issuer === right.issuer && left.subject === right.subject;
const boundPrincipal = (model: OperationModel) =>
  model.bindingSource.kind === "AUTHORITY" ? model.bindingSource.principal : undefined;
const retained = (model: OperationModel) =>
  model.phase !== "ACCEPTED" && (model.dirty || model.prepared !== null || model.priorUncertain);
const actorValues = (
  document: PersonDocument,
  kind: string,
  values: FormValues = {},
): FormValues => ({
  ...values,
  ...(kind === "SET_PROFILE" && document.context.profileSeed !== null
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
        return task?.kind === "OPERATION" && task.subject === "AUTHORITY" ? [task.act] : [];
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
        model.discard(false, "ADMIT", {}, { kind: "AUTHORITY" });
      }
      return;
    }
    const kind = authorityPrimaryAction(source.offered);
    if (kind !== undefined) {
      seeded.current = source.key;
      model.discard(false, kind, actorValues(source.document, kind), {
        kind: "AUTHORITY",
        principal: source.document.context.principal,
      });
    }
  }, [model, source, fixed]);
};
const admissionBinding = (model: OperationModel, principal: PrincipalInput | null) =>
  principal !== null &&
  model.entry.kind === "ADMIT" &&
  samePrincipal(boundPrincipal(model), principal);
const personBinding = (model: OperationModel, source: Snapshot | null): source is Snapshot =>
  source !== null &&
  samePrincipal(boundPrincipal(model), source.document.context.principal) &&
  model.entry.kind !== "ADMIT" &&
  model.entry.kind !== "REGISTER";
const defaultAuthorityKind = (source: Snapshot | null) =>
  source === null ? "ADMIT" : (authorityPrimaryAction(source.offered) ?? "ADMIT");
const matchingIntent = (intent: Intent | null, kind: string, epoch: string, available: boolean) =>
  intent?.kind === kind && intent.epoch === epoch && available ? intent : null;
const discardBinding = (
  kind: string,
  principal: PrincipalInput | null,
  source: Snapshot | null,
): Binding => {
  if (principal !== null) {
    return { kind: "AUTHORITY", principal };
  }
  if (source === null || kind === "ADMIT" || kind === "REGISTER") {
    return { kind: "AUTHORITY" };
  }
  return { kind: "AUTHORITY", principal: source.document.context.principal };
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
export const useAuthorityTarget = (
  model: OperationModel,
  revision: number,
  available: boolean,
  admissionPrincipal: PrincipalInput | null = null,
) => {
  const { source, epoch, actorId, observe } = useActorDisclosure(revision, available);
  const [intent, setIntent] = useState<Intent | null>(null);
  const admissionBound = admissionBinding(model, admissionPrincipal);
  const actorBound = personBinding(model, source);
  const fixed = admissionBound || actorBound;
  useActorDefault(model, source, fixed);
  const begin = (kind: string, values: FormValues = {}, grant?: AuthorityGrantInput) => {
    if (source === null || !source.offered.includes(kind) || model.busy) {
      return;
    }
    const binding: Binding = {
      kind: "AUTHORITY",
      principal: source.document.context.principal,
      ...(grant === undefined ? {} : { grant }),
    };
    if (retained(model)) {
      setIntent({ epoch, source, kind, values, binding });
      model.requestDiscard(kind);
    } else {
      model.discard(false, kind, actorValues(source.document, kind, values), binding);
    }
  };
  const discard: OperationModel["discard"] = (acknowledged, kind, values, explicitBinding) => {
    const nextKind = kind ?? defaultAuthorityKind(source);
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
        (selected === null ? {} : actorValues(selected.document, nextKind, pending?.values)),
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
    primary: authorityPrimaryAction(source?.offered ?? []),
    person: actorBound ? source.document : null,
    mode: targetMode(actorId, fixed, retained(model)),
  };
};
export type AuthorityTarget = ReturnType<typeof useAuthorityTarget>;
