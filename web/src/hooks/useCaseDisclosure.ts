import { useCallback, useEffect, useRef, useState } from "react";
import { service, resultNotice, type ServiceResponse } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";
import type { Notice } from "../api/notices";

type CaseView = Extract<ServiceResponse<"case.get">["outcome"], { tag: "SUCCEEDED" }>["data"];
type Disclosure = {
  value: CaseView | null;
  historyModes: CaseView["advice"]["history"];
  message: Notice | null;
  loading: boolean;
};

export const useCaseDisclosure = (caseReference: string, token: string) => {
  const active = useRef<AbortController | null>(null);
  const [revision, setRevision] = useState(0);
  const [current, setCurrent] = useState<Disclosure>({
    value: null,
    historyModes: [],
    message: null,
    loading: true,
  });
  const revalidate = useCallback(() => {
    active.current?.abort();
    setCurrent((previous) => ({ ...previous, value: null, message: null, loading: true }));
    setRevision((previous) => previous + 1);
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    active.current = controller;
    void service.get(caseReference, token, controller.signal).then((result) => {
      if (controller.signal.aborted) {
        return;
      }
      const value =
        result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED"
          ? result.value.outcome.data
          : null;
      setCurrent((previous) => ({
        value,
        historyModes:
          value?.advice.history ?? (isDisclosureRefused(result) ? [] : previous.historyModes),
        message: value === null ? resultNotice(result) : null,
        loading: false,
      }));
    });
    return () => {
      controller.abort();
    };
    // oxlint-disable-next-line react/exhaustive-effect-dependencies -- The revision explicitly requests a fresh authoritative read after history refusal.
  }, [caseReference, token, revision]);
  return { ...current, revalidate };
};
