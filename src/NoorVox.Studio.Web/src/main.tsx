import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";
import { App } from "./App";
import { StudioProvider } from "./studio";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <StudioProvider>
      <App />
    </StudioProvider>
  </StrictMode>,
);
