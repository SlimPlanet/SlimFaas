import { Navigate, Route, Routes } from "react-router-dom";
import Layout from "./components/layout/Layout";
import SwaggerToMcpPage from "./pages/swagger/SwaggerToMcpPage";
import ChatPage from "./pages/chat/ChatPage";

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Navigate to="/swagger-to-mcp" replace />} />
        <Route path="/swagger-to-mcp" element={<SwaggerToMcpPage />} />
        <Route path="/chat" element={<ChatPage />} />
        <Route path="*" element={<Navigate to="/swagger-to-mcp" replace />} />
      </Routes>
    </Layout>
  );
}
