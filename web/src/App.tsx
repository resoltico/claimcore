import { useEffect, useState } from "react";
import { useRoute } from "./routing/useRoute";
import { MissingPage } from "./views/MissingPage";
import { usePresentation } from "./presentation/context";
import { LoginScreen } from "./views/LoginScreen";
import { Dashboard } from "./views/Dashboard";
import { useSessionContext } from "./session/SessionContext";
import { SessionProvider } from "./session/SessionProvider";
import { Message, NoticeView } from "./presentation/Message";
const SessionFeedback = () => {
  const { state, refresh } = useSessionContext();
  if (state.kind === "loading") {
    return (
      <main className="loading" role="status">
        <Message id="ui.loadingSession" />
      </main>
    );
  }
  if (state.kind === "failure") {
    return (
      <main className="loading">
        <p role="alert">
          <NoticeView value={state.message} />
        </p>
        <button type="button" onClick={() => void refresh()}>
          <Message id="ui.tryAgain" />
        </button>
      </main>
    );
  }
  return state.kind === "anonymous" ? <LoginScreen message={state.message} /> : null;
};
const SessionContent = () => {
  const { state } = useSessionContext();
  const [established, setEstablished] = useState(false);
  const authenticated = state.kind === "authenticated";
  if (authenticated && !established) {
    setEstablished(true);
  }
  return (
    <>
      <div hidden={!authenticated} inert={!authenticated}>
        {established ? <Dashboard /> : null}
      </div>
      {authenticated ? null : <SessionFeedback />}
    </>
  );
};
export const App = () => {
  const route = useRoute();
  const p = usePresentation();
  useEffect(() => {
    document.title =
      route.screen === "missing" ? `${p.text("ui.pageNotFound")} · ClaimCore` : "ClaimCore";
  }, [route.screen, p]);
  if (route.screen === "missing") {
    return <MissingPage />;
  }
  return (
    <SessionProvider>
      <SessionContent />
    </SessionProvider>
  );
};
