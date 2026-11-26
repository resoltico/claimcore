import { guardedDeparture } from "../routing/navigation";
import { useEffect, useState, type ComponentType } from "react";
import { usePresentation } from "../presentation/context";

type State<Props> = { component: ComponentType<Props> | null; failed: boolean; attempt: number };
/** Loading code never suspends the authenticated workspace or its operation coordinators. */
export const ViewModule = <Props extends object>({
  load,
  viewProps,
}: {
  load: () => Promise<ComponentType<Props>>;
  viewProps: Props;
}) => {
  const p = usePresentation();
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<State<Props>>({ component: null, failed: false, attempt: 0 });
  useEffect(() => {
    let current = true;
    void load()
      .then((component) => {
        if (current) {
          setState({ component, failed: false, attempt });
        }
      })
      .catch(() => {
        if (current) {
          setState({ component: null, failed: true, attempt });
        }
      });
    return () => {
      current = false;
    };
  }, [load, attempt]);
  if (state.component !== null && state.attempt === attempt) {
    const Component = state.component;
    return <Component {...viewProps} />;
  }
  return (
    <section role={state.failed ? "alert" : "status"}>
      <p>{p.text(state.failed ? "ui.viewLoadFailed" : "ui.viewLoading")}</p>
      {state.failed ? (
        <div className="actions">
          <button
            type="button"
            onClick={() => {
              setState({ component: null, failed: false, attempt: attempt + 1 });
              setAttempt((value) => value + 1);
            }}
          >
            {p.text("ui.retryView")}
          </button>
          <button
            type="button"
            onClick={() => {
              guardedDeparture(() => {
                window.location.reload();
              });
            }}
          >
            {p.text("ui.reloadInterface")}
          </button>
        </div>
      ) : null}
    </section>
  );
};
