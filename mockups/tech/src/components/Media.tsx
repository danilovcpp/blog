import { useEffect, useRef, useState, type ReactNode } from "react";
import {
    Download,
    FileImage,
    FileText,
    FolderOpen,
    Upload,
} from "lucide-react";
import { localId } from "../mock/data";
import {
    fileLimits,
    fileSize,
    imageAccept,
    prepareMedia,
    UploadExpired,
    type LibraryPurpose,
    type MediaFile,
    type MediaPurpose,
} from "../mock/media";
import { delay, useMock } from "../mock/store";
import { Alert, Button, Field, Modal } from "./ui";

type UploadState =
    | { phase: "sending"; name: string; progress: number }
    | { phase: "processing" | "ready"; name: string }
    | { phase: "retry"; name: string }
    | { phase: "error"; message: string };

export function MediaUpload({
    purpose,
    label,
    onReady,
    disabled,
    onActive,
    actions,
}: {
    purpose: MediaPurpose;
    label: string;
    onReady: (file: MediaFile) => void;
    disabled?: boolean;
    onActive?: (value: boolean) => void;
    actions?: ReactNode;
}) {
    const { media, scenario } = useMock();
    const [state, setState] = useState<UploadState | null>(null);
    const input = useRef<HTMLInputElement>(null);
    const job = useRef<ReturnType<typeof media.reserve> | null>(null);
    const expiryTimer = useRef<ReturnType<typeof setTimeout> | undefined>(
        undefined,
    );
    const callbacks = useRef({ onReady, onActive });
    callbacks.current = { onReady, onActive };
    const release = () => {
        clearTimeout(expiryTimer.current);
        if (job.current) media.release(job.current.id);
        job.current = null;
        callbacks.current.onActive?.(false);
    };
    useEffect(
        () => () => {
            if (job.current) release();
        },
        [],
    );
    const run = async (
        upload: NonNullable<typeof job.current>,
        retry = false,
    ) => {
        const { source } = upload;
        callbacks.current.onActive?.(true);
        setState({ phase: "sending", name: source.name, progress: 0 });
        try {
            for (const progress of [20, 48, 77, 100]) {
                await delay(260);
                if (job.current !== upload) return;
                media.checkUpload(upload);
                if (!retry && scenario === "media-expired" && progress === 48)
                    throw new UploadExpired();
                if (
                    progress === 48 &&
                    ((!retry && scenario === "media-interrupted") ||
                        scenario === "offline" ||
                        scenario === "unavailable")
                ) {
                    setState({ phase: "retry", name: source.name });
                    callbacks.current.onActive?.(false);
                    return;
                }
                setState({ phase: "sending", name: source.name, progress });
            }
            await delay(160);
            if (job.current !== upload) return;
            setState({ phase: "processing", name: source.name });
            const [prepared] = await Promise.all([
                prepareMedia(source, purpose),
                delay(650),
            ]);
            if (job.current !== upload) return;
            const file = media.complete(upload, prepared);
            release();
            setState({ phase: "ready", name: file.name });
            callbacks.current.onReady(file);
        } catch (error) {
            if (job.current !== upload) return;
            release();
            setState({ phase: "error", message: (error as Error).message });
        }
    };
    const start = (file: File) => {
        release();
        try {
            const upload = media.reserve(file, purpose);
            job.current = upload;
            expiryTimer.current = setTimeout(() => {
                if (job.current !== upload) return;
                release();
                setState({
                    phase: "error",
                    message: new UploadExpired().message,
                });
            }, upload.expiresAt - Date.now());
            void run(upload);
        } catch (error) {
            setState({ phase: "error", message: (error as Error).message });
        }
    };
    const running = state?.phase === "sending" || state?.phase === "processing";
    const cancel = () => {
        release();
        setState(null);
    };
    return (
        <div className="media-upload">
            <input
                ref={input}
                type="file"
                aria-label={label}
                accept={purpose === "Attachment" ? undefined : imageAccept}
                hidden
                onChange={(e) => {
                    const file = e.target.files?.[0];
                    e.target.value = "";
                    if (file) start(file);
                }}
            />
            <div className="media-actions">
                <Button
                    type="button"
                    variant="secondary"
                    disabled={disabled || running}
                    onClick={() => input.current?.click()}
                >
                    <Upload size={16} />
                    {label}
                </Button>
                {actions}
            </div>
            {state && (
                <div className="media-upload-feedback">
                    {state.phase === "error" ? (
                        <Alert tone="error">{state.message}</Alert>
                    ) : state.phase === "retry" ? (
                        <>
                            <Alert tone="error">
                                Передача прервалась. Повторите загрузку файла «
                                {state.name}» или выберите другой файл.
                            </Alert>
                            <div className="media-actions">
                                <Button
                                    type="button"
                                    disabled={disabled}
                                    onClick={() =>
                                        job.current &&
                                        void run(job.current, true)
                                    }
                                >
                                    Повторить
                                </Button>
                                <Button
                                    type="button"
                                    variant="ghost"
                                    onClick={cancel}
                                >
                                    Отменить загрузку
                                </Button>
                            </div>
                        </>
                    ) : state.phase === "ready" ? (
                        <Alert tone="success">Готово: {state.name}</Alert>
                    ) : (
                        <div className="media-progress">
                            <div role="status">
                                <strong>
                                    {state.phase === "sending"
                                        ? "Передача файла"
                                        : "Обработка"}
                                </strong>
                                <span>{state.name}</span>
                            </div>
                            <progress
                                aria-label={
                                    state.phase === "sending"
                                        ? "Передача файла"
                                        : "Обработка"
                                }
                                max={100}
                                value={
                                    state.phase === "sending"
                                        ? state.progress
                                        : undefined
                                }
                            />
                            {state.phase === "sending" && (
                                <span className="tiny">{state.progress}%</span>
                            )}
                            <Button
                                type="button"
                                variant="ghost"
                                onClick={cancel}
                            >
                                Отменить загрузку
                            </Button>
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}

export function MediaRow({
    file,
    children,
}: {
    file: MediaFile;
    children: ReactNode;
}) {
    const Icon = file.purpose === "Attachment" ? FileText : FileImage;
    return (
        <li className="media-row">
            <div className="media-row-file">
                {file.purpose === "PublicationImage" ? (
                    <img className="media-file-icon" src={file.url} alt="" />
                ) : (
                    <span className="media-file-icon">
                        <Icon size={19} />
                    </span>
                )}
                <div className="media-row-label">
                    <strong>{file.name}</strong>
                    <small>{fileSize(file.size)}</small>
                </div>
            </div>
            <div className="media-row-actions">{children}</div>
        </li>
    );
}

export function ArticleAttachments({ files }: { files: MediaFile[] }) {
    if (!files.length) return null;
    return (
        <section
            className="article-attachments"
            aria-labelledby="article-attachments"
        >
            <h2 id="article-attachments">Вложения</h2>
            <ul className="media-list">
                {files.map((file) => (
                    <MediaRow key={file.id} file={file}>
                        <a
                            className="button button-secondary"
                            href={file.url}
                            download={file.name}
                            aria-label={"Скачать " + file.name}
                        >
                            <Download size={16} /> Скачать
                        </a>
                    </MediaRow>
                ))}
            </ul>
        </section>
    );
}

export function MediaLibrary({
    ownerId,
    purpose,
    onChoose,
    onClose,
    selectedIds,
    draftIds,
}: {
    ownerId: string;
    purpose: LibraryPurpose;
    onChoose: (file: MediaFile) => void;
    onClose: () => void;
    selectedIds: string[];
    draftIds: string[];
}) {
    const { media, hasRole } = useMock();
    const [filter, setFilter] = useState<LibraryPurpose | "all">(purpose);
    const [error, setError] = useState<{ id: string; message: string } | null>(
        null,
    );
    const files = media
        .library(ownerId)
        .filter((file) => filter === "all" || file.purpose === filter);
    const allowance = media.allowance(ownerId);
    const canUpload = ownerId === localId && hasRole("Author");
    const uploadPurpose = purpose;
    return (
        <Modal open wide onClose={onClose} title="Библиотека файлов">
            <div className="media-library">
                <p className="media-note">
                    {purpose === "Attachment"
                        ? "Выберите вложение"
                        : "Выберите изображение"}{" "}
                    из готовых файлов владельца статьи.
                </p>
                <div className="media-quota">
                    <div className="media-quota-head">
                        <strong>Место в библиотеке</strong>
                        <span>До {fileSize(allowance.limit)}</span>
                    </div>
                    <div className="media-quota-meter" aria-hidden="true">
                        <span
                            style={{
                                width:
                                    (allowance.used / allowance.limit) * 100 +
                                    "%",
                            }}
                        />
                        <span
                            style={{
                                width:
                                    (allowance.reserved / allowance.limit) *
                                        100 +
                                    "%",
                            }}
                        />
                    </div>
                    <dl>
                        <div>
                            <dt>Занято</dt>
                            <dd>{fileSize(allowance.used)}</dd>
                        </div>
                        <div>
                            <dt>Зарезервировано</dt>
                            <dd>{fileSize(allowance.reserved)}</dd>
                        </div>
                        <div>
                            <dt>Доступно</dt>
                            <dd>{fileSize(allowance.available)}</dd>
                        </div>
                    </dl>
                </div>
                <div className="media-library-tools">
                    <Field label="Назначение" name="media-purpose">
                        <select
                            id="media-purpose"
                            value={filter}
                            onChange={(e) => {
                                setFilter(e.target.value as typeof filter);
                                setError(null);
                            }}
                        >
                            <option value="all">Все файлы</option>
                            <option value="Attachment">Вложения</option>
                            <option value="PublicationImage">
                                Изображения публикаций
                            </option>
                        </select>
                    </Field>
                    {canUpload && (
                        <MediaUpload
                            key={uploadPurpose}
                            purpose={uploadPurpose}
                            label={
                                uploadPurpose === "Attachment"
                                    ? "Загрузить файл"
                                    : "Загрузить изображение"
                            }
                            onReady={() => {}}
                        />
                    )}
                </div>
                {canUpload && uploadPurpose === "PublicationImage" && (
                    <p className="media-note">
                        JPEG, PNG или WebP · до {fileLimits.PublicationImage}{" "}
                        МиБ
                    </p>
                )}
                {files.length ? (
                    <ul className="media-list">
                        {files.map((file) => (
                            <MediaRow file={file} key={file.id}>
                                {file.purpose === purpose && (
                                    <Button
                                        type="button"
                                        variant="secondary"
                                        disabled={selectedIds.includes(file.id)}
                                        onClick={() => onChoose(file)}
                                    >
                                        {selectedIds.includes(file.id)
                                            ? "Добавлено"
                                            : "Выбрать"}
                                    </Button>
                                )}
                                <button
                                    className="danger-link"
                                    onClick={() => {
                                        try {
                                            if (draftIds.includes(file.id))
                                                throw new Error(
                                                    "Файл выбран для этой статьи. Сначала уберите его из статьи и сохраните изменения.",
                                                );
                                            media.remove(file.id);
                                            setError(null);
                                        } catch (e) {
                                            setError({
                                                id: file.id,
                                                message: (e as Error).message,
                                            });
                                        }
                                    }}
                                >
                                    Удалить файл
                                </button>
                                {error?.id === file.id && (
                                    <Alert tone="error">{error.message}</Alert>
                                )}
                            </MediaRow>
                        ))}
                    </ul>
                ) : (
                    <div className="media-empty">
                        <FolderOpen size={20} />
                        <div>
                            <strong>Здесь пока нет готовых файлов</strong>
                            <p>
                                {canUpload
                                    ? "Загрузите файл или измените назначение."
                                    : "Попробуйте другое назначение."}
                            </p>
                        </div>
                    </div>
                )}
                <div className="modal-footer">
                    <Button variant="secondary" onClick={onClose}>
                        Закрыть
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
