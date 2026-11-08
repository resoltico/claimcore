import { useRequesterContext } from "../../hooks/useRequesterContext";
import type { CurrentCase } from "../../api/service";
import { isCaseAct, type ActKind } from "../../operation/metadata";
import { RegionView } from "../../ui/regions/RegionView";
import { Actions } from "../../ui/regions/Actions";

const recommendation = (current: CurrentCase) =>
  current.view.regions.find((region) => region.role === "RECOMMENDATION");
export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => {
  const requester = useRequesterContext();
  const region = recommendation(current);
  if (
    region === undefined ||
    current.advice.maintenance.active ||
    requester?.maintenance.active === true
  ) {
    return null;
  }
  return (
    <RegionView
      region={region}
      onAct={(act) => {
        if (isCaseAct(act)) {
          onAct(act);
        }
      }}
    />
  );
};
export const CaseActionAlternatives = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => {
  const requester = useRequesterContext();
  const content = recommendation(current)?.content;
  if (
    content?.kind !== "ACTIONS" ||
    current.advice.maintenance.active ||
    requester?.maintenance.active === true
  ) {
    return null;
  }
  return (
    <Actions
      content={{ ...content, recommended: null }}
      expandOther
      onAct={(act) => {
        if (isCaseAct(act)) {
          onAct(act);
        }
      }}
    />
  );
};
