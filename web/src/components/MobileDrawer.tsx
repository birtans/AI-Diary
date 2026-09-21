import { NavLink, useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { Sidebar } from "./Sidebar";
import { ThemeToggle } from "./ThemeToggle";

interface Props {
  isAdmin: boolean;
  onNavigate: () => void;
}

/**
 * The full mobile navigation surface, opened via the topbar hamburger: app
 * destinations, the note list, and account controls — everything the desktop
 * topbar shows inline, stacked into one slide-in panel.
 */
export function MobileDrawer({ isAdmin, onNavigate }: Props) {
  const { userEmail, logout } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const selectedId = location.pathname === "/" ? searchParams.get("note") : null;

  return (
    <nav className="mobile-drawer">
      <div className="mobile-drawer-nav">
        <NavLink to="/" end onClick={onNavigate}>
          Notes
        </NavLink>
        <NavLink to="/search" onClick={onNavigate}>
          Search
        </NavLink>
        <NavLink to="/chat" onClick={onNavigate}>
          Chat
        </NavLink>
        <NavLink to="/settings" onClick={onNavigate}>
          Settings
        </NavLink>
        {isAdmin && (
          <NavLink to="/admin" onClick={onNavigate}>
            Admin
          </NavLink>
        )}
      </div>

      <Sidebar
        selectedId={selectedId}
        onSelect={(id) => {
          navigate(`/?note=${id}`);
          onNavigate();
        }}
        onCreate={(id) => {
          navigate(`/?note=${id}&new=1`);
          onNavigate();
        }}
        onDeleted={(id) => {
          if (selectedId === id) navigate("/");
        }}
      />

      <div className="mobile-drawer-user">
        <ThemeToggle />
        {userEmail && (
          <>
            <span className="user-avatar">{userEmail.slice(0, 2).toUpperCase()}</span>
            <span className="muted">{userEmail}</span>
          </>
        )}
        <button type="button" className="link-button" onClick={() => void logout()}>
          Sign out
        </button>
      </div>
    </nav>
  );
}
