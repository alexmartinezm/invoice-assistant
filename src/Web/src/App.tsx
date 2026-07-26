import { useState } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { AppHeader } from './components/AppHeader';
import { ChatDrawer } from './chat/ChatDrawer';
import { InvoicesPage } from './pages/InvoicesPage';
import { LoginPage } from './pages/LoginPage';
import { useAuth } from './auth/useAuth';

export default function App() {
  const { session } = useAuth();

  if (!session) {
    return (
      <Routes>
        <Route path="*" element={<LoginPage />} />
      </Routes>
    );
  }

  return <SignedInApp />;
}

function SignedInApp() {
  // The drawer is always mounted on large screens and slides over on small ones, so a
  // conversation survives navigating between pages.
  const [chatOpen, setChatOpen] = useState(false);

  return (
    <div className="grid min-h-dvh lg:grid-cols-[minmax(0,1fr)_26rem]">
      <div className="min-w-0">
        <AppHeader onOpenChat={() => setChatOpen(true)} />

        <Routes>
          <Route path="/invoices" element={<InvoicesPage />} />
          <Route path="*" element={<Navigate to="/invoices" replace />} />
        </Routes>
      </div>

      <ChatDrawer open={chatOpen} onClose={() => setChatOpen(false)} />
    </div>
  );
}
