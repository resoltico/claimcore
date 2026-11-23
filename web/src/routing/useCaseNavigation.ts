import type { Link, CaseSelector } from "@contracts/client";
import { caseTargetPath, coreLinkPath, reminderLinkTarget } from "./coreLinks";
import { useState } from "react";
import { navigate } from "./navigation";
/** Return context belongs only to this verified case workspace, never browser history or a journal. */
export const useCaseNavigation = (owner: object | null) => {
  const [returnTo, setReturnTo] = useState<{
    owner: object;
    url: string;
    scrollY: number;
    focusId: string;
  } | null>(null);
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
