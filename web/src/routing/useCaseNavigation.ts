import type { Affordance, Link, CaseSelector } from "@contracts/client";
import { caseTargetPath, coreLinkPath, reminderLinkTarget } from "./coreLinks";
import { useState } from "react";
import { actPath, parseRoute } from "./routes";
import type { CurrentCase } from "../protocol/types";
import { navigate } from "./navigation";
/** Return context belongs only to this verified case workspace, never browser history or a journal. */
export const useCaseNavigation = (owner: object | null) => {
  const [returnTo, setReturnTo] = useState<{
    owner: object;
    url: string;
    scrollY: number;
    focusId: string;
  } | null>(null);
  const [actOrigin, setActOrigin] = useState<{
    owner: object;
    url: string;
    focusId?: string;
    scrollY: number;
  } | null>(null);
  const creationOrigin =
    returnTo?.owner === owner && returnTo.focusId === "open-case" ? returnTo.url : "/cases";
  const originList = new URL(creationOrigin, location.origin);
  const remember = (focusId: string) => {
    if (owner !== null) {
      setReturnTo({
        owner,
        url: location.pathname + location.search,
        scrollY: window.scrollY,
        focusId,
      });
    }
  };
  return {
    openCaseQuery: originList.pathname === "/cases" ? originList.search : "",
    openAct: (current: CurrentCase, action: Affordance, focusId?: string) => {
      const route = parseRoute(location);
      const target =
        route.screen === "case" || route.screen === "caseAct"
          ? route.target
          : { kind: "ID" as const, caseId: current.subject.caseId };
      const path = actPath(target, action.task, action.binding);
      const origin =
        route.screen === "case"
          ? {
              url: location.pathname + location.search,
              ...(focusId === undefined ? {} : { focusId }),
              scrollY: window.scrollY,
            }
          : null;
      if (
        navigate(path, false, undefined, undefined, focusId) &&
        origin !== null &&
        owner !== null
      ) {
        setActOrigin({ owner, ...origin });
      }
    },
    closeAct: (target: CaseSelector) => {
      const origin = actOrigin?.owner === owner ? actOrigin : null;
      if (
        navigate(
          origin?.url ?? caseTargetPath(target),
          true,
          origin?.focusId ?? "case-detail-title",
          origin?.scrollY,
        )
      ) {
        setActOrigin(null);
      }
    },
    linkNavigation: (link: Link) => {
      const selected = reminderLinkTarget(link);
      if (selected === undefined) {
        return undefined;
      }
      const path = coreLinkPath(link);
      const focusId = `reminder-link-${selected.reminderId}`;
      return {
        id: focusId,
        follow: () => {
          remember(focusId);
          if (path !== undefined) {
            navigate(path);
          }
        },
      };
    },
    selectCase: (target: CaseSelector, originId?: string) => {
      if (originId !== undefined) {
        document.getElementById(originId)?.focus({ preventScroll: true });
      }
      remember(originId ?? (target.kind === "ID" ? `case-${target.caseId}` : "case-lookup"));
      navigate(caseTargetPath(target));
    },
    openCase: () => {
      remember("open-case");
      navigate("/cases/new");
    },
    backToCases: (worklist?: string) => {
      const owned = returnTo?.owner === owner ? returnTo : null;
      const next =
        worklist ??
        owned?.url ??
        (location.pathname.includes("/reminders/") ? "/reminders" : "/cases");
      const position = owned?.url === next ? owned : null;
      navigate(next, false, position?.focusId, position?.scrollY);
    },
  };
};
