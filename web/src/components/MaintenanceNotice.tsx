import { usePresentation } from "../presentation/context";

export const MaintenanceNotice = ({ active }: { active: boolean }) => {
  const p = usePresentation();
  return active ? (
    <p className="maintenance-notice" role="status">
      {p.text("diagnostic.REGISTER_MAINTENANCE")}
    </p>
  ) : null;
};
