import { brand } from "./brands";
import { lazy, Suspense, useEffect } from "react";
import { Route, Routes, useLocation } from "react-router-dom";
import { AppLayout, DemoTools, PublicLayout } from "./components/Layout";
import { Guard, Toasts, Skeleton } from "./components/ui";
import { Journal, Topics, About, NotFound } from "./pages/Journal";
const Article = lazy(() =>
    import("./pages/Article").then((m) => ({ default: m.Article })),
);
const CommentManage = lazy(() =>
    import("./pages/Article").then((m) => ({ default: m.CommentManage })),
);
const Workspace = lazy(() =>
    import("./pages/Workspace").then((m) => ({ default: m.Workspace })),
);
const Editor = lazy(() =>
    import("./pages/Workspace").then((m) => ({ default: m.Editor })),
);
const Moderation = lazy(() =>
    import("./pages/Moderation").then((m) => ({ default: m.Moderation })),
);
const Taxonomy = lazy(() =>
    import("./pages/Moderation").then((m) => ({ default: m.Taxonomy })),
);
const AuthPage = lazy(() =>
    import("./pages/Identity").then((m) => ({ default: m.AuthPage })),
);
const Profile = lazy(() =>
    import("./pages/Identity").then((m) => ({ default: m.Profile })),
);
const Security = lazy(() =>
    import("./pages/Identity").then((m) => ({ default: m.Security })),
);
const Sessions = lazy(() =>
    import("./pages/Identity").then((m) => ({ default: m.Sessions })),
);
const Deactivate = lazy(() =>
    import("./pages/Identity").then((m) => ({ default: m.Deactivate })),
);
const Prototype = lazy(() =>
    import("./pages/Prototype").then((m) => ({ default: m.Prototype })),
);
const ViewportPreview = lazy(() =>
    import("./pages/Prototype").then((m) => ({ default: m.ViewportPreview })),
);

export default function App() {
    const location = useLocation();
    useEffect(() => {
        window.scrollTo({ top: 0, behavior: "instant" });
        document.title = location.pathname.startsWith("/auth")
            ? `${brand.name} — ваш аккаунт`
            : `${brand.name} — ${brand.documentTitle}`;
    }, [location.pathname]);
    return (
        <>
            <a className="skip-link" href="#main-content">
                К содержимому
            </a>
            <div id="main-content">
                <Suspense
                    fallback={
                        <main className="container">
                            <Skeleton />
                        </main>
                    }
                >
                    <Routes>
                        <Route
                            path="prototype/responsive"
                            element={<ViewportPreview />}
                        />
                        <Route element={<PublicLayout />}>
                            <Route index element={<Journal />} />
                            <Route path="search" element={<Journal />} />
                            <Route path="bookmarks" element={<Journal />} />
                            <Route
                                path="authors/:authorId"
                                element={<Journal />}
                            />
                            <Route path="topics" element={<Topics />} />
                            <Route
                                path="category/:categorySlug"
                                element={<Journal />}
                            />
                            <Route path="tag/:tagSlug" element={<Journal />} />
                            <Route path="posts/:slug" element={<Article />} />
                            <Route path="about" element={<About />} />
                            <Route path="prototype" element={<Prototype />} />
                            <Route
                                path="comments/:id/manage"
                                element={
                                    <Guard>
                                        <CommentManage />
                                    </Guard>
                                }
                            />
                            <Route path="*" element={<NotFound />} />
                        </Route>
                        <Route
                            element={
                                <Guard>
                                    <AppLayout />
                                </Guard>
                            }
                        >
                            <Route
                                path="workspace"
                                element={
                                    <Guard role="Author">
                                        <Workspace />
                                    </Guard>
                                }
                            />
                            <Route
                                path="moderation"
                                element={
                                    <Guard role="Moderator">
                                        <Moderation />
                                    </Guard>
                                }
                            />
                            <Route
                                path="admin/taxonomy"
                                element={
                                    <Guard role="Admin">
                                        <Taxonomy />
                                    </Guard>
                                }
                            />
                        </Route>
                        <Route
                            element={
                                <Guard>
                                    <AppLayout account />
                                </Guard>
                            }
                        >
                            <Route
                                path="account/profile"
                                element={<Profile />}
                            />
                            <Route
                                path="account/security"
                                element={<Security />}
                            />
                            <Route
                                path="account/sessions"
                                element={<Sessions />}
                            />
                            <Route
                                path="account/deactivate"
                                element={<Deactivate />}
                            />
                        </Route>
                        <Route
                            path="workspace/new"
                            element={
                                <Guard role="Author">
                                    <Editor key="new" />
                                </Guard>
                            }
                        />
                        <Route
                            path="workspace/:id/edit"
                            element={
                                <Guard role="Author">
                                    <Editor key={location.pathname} />
                                </Guard>
                            }
                        />
                        <Route
                            path="auth/login"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="login"
                                />
                            }
                        />
                        <Route
                            path="auth/register"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="register"
                                />
                            }
                        />
                        <Route
                            path="auth/check-email"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="check-email"
                                />
                            }
                        />
                        <Route
                            path="auth/verify-email"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="verify-email"
                                />
                            }
                        />
                        <Route
                            path="auth/resend-verification"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="resend-verification"
                                />
                            }
                        />
                        <Route
                            path="auth/forgot-password"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="forgot-password"
                                />
                            }
                        />
                        <Route
                            path="auth/reset-password"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="reset-password"
                                />
                            }
                        />
                        <Route
                            path="auth/accept-invitation"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="accept-invitation"
                                />
                            }
                        />
                        <Route
                            path="auth/session-expired"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="session-expired"
                                />
                            }
                        />
                        <Route
                            path="auth/deactivated"
                            element={
                                <AuthPage
                                    key={location.pathname}
                                    kind="deactivated"
                                />
                            }
                        />
                    </Routes>
                </Suspense>
            </div>
            <DemoTools />
            <Toasts />
        </>
    );
}
