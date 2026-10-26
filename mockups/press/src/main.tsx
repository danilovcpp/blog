import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import App from "./App";
import { MockProvider } from "./mock/store";
import { applyTheme } from "./themes";
import "./styles.css";

applyTheme();
const router = createBrowserRouter([{ path: "*", element: <App /> }]);
ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
        <MockProvider>
            <RouterProvider router={router} />
        </MockProvider>
    </React.StrictMode>,
);
