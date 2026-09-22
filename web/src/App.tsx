import { LoginScreen } from "./views/LoginScreen";
import { Dashboard } from "./views/Dashboard";
import { useSession } from "./hooks/useSession";
import { Message, NoticeView } from "./presentation/Message";
import { PresentationControls } from "./presentation/PresentationControls";

const SessionContent = () => {
  const { state, login, logout, refresh } = useSession();
  if (state.kind === "loading")
    return (
      <main className="loading">
        <Message id="ui.loadingSession" />
      </main>
    );
  if (state.kind === "failure")
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
  if (state.kind === "authenticated")
    return <Dashboard token={state.token} sessionEpoch={state.epoch} onLogout={logout} />;
  return (
    <LoginScreen tokenAvailable={state.token !== null} message={state.message} onLogin={login} />
  );
};
export const App = () => (
  <>
    <header className="presentation-bar">
      <PresentationControls />
    </header>
    <SessionContent />
  </>
);
