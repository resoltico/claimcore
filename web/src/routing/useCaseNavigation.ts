import { useState } from "react";
import type { CaseSelector } from "@contracts/client";
import { casePath, referencePath } from "./routes";
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
    selectCase: (target: CaseSelector) => {
      remember(target.kind === "ID" ? `case-${target.caseId}` : "case-lookup");
      navigate(
        target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference),
      );
    },
    openCase: () => {
      remember("open-case");
      navigate("/cases/new");
    },
    backToCases: () => {
      const owned = returnTo?.owner === owner ? returnTo : null;
      navigate(owned?.url ?? "/cases", false, owned?.focusId, owned?.scrollY);
    },
  };
};
