import { DocumentTitleScope } from "../hooks/useDocumentTitle";
import { useState } from "react";
import { useRoute } from "../routing/useRoute";
import { MissingPage } from "./MissingPage";
import { LoginScreen } from "./LoginScreen";
import { Dashboard } from "./Dashboard";
import { useSessionContext } from "../session/SessionContext";
import { SessionProvider } from "../session/SessionProvider";
import { Message, NoticeView } from "../presentation/Message";
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
  const route = useRoute();
  const [established, setEstablished] = useState(false);
  const authenticated = state.kind === "authenticated";
  if (authenticated && !established) {
    setEstablished(true);
  }
  return (
    <>
      <div hidden={!authenticated} inert={!authenticated}>
        <DocumentTitleScope active={authenticated}>
          {established ? <Dashboard /> : null}
        </DocumentTitleScope>
      </div>
      {!authenticated && route.screen === "missing" ? <MissingPage /> : null}
      {!authenticated && route.screen !== "missing" ? <SessionFeedback /> : null}
    </>
  );
};
export const App = () => (
  <SessionProvider>
    <SessionContent />
  </SessionProvider>
);
