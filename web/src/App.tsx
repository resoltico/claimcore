import { LoginScreen } from "./views/LoginScreen";
import { Dashboard } from "./views/Dashboard";
import { useSessionContext } from "./session/SessionContext";
import { SessionProvider } from "./session/SessionProvider";
import { Message, NoticeView } from "./presentation/Message";
const SessionContent = () => {
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
  if (state.kind === "authenticated") {
    return <Dashboard key={`dashboard:${state.epoch}`} />;
  }
  return <LoginScreen message={state.message} />;
};
export const App = () => (
  <SessionProvider>
    <SessionContent />
  </SessionProvider>
);
