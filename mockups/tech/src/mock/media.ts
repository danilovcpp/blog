import { useRef, useState } from "react";
import { localId, otherId, uid, type Post } from "./data";

export type MediaPurpose = "Attachment" | "PublicationImage" | "Avatar";
export type LibraryPurpose = Exclude<MediaPurpose, "Avatar">;
export type MediaFile = {
    id: string;
    ownerId: string;
    name: string;
    size: number;
    purpose: MediaPurpose;
    url: string;
    smallUrl?: string;
};
export const MiB = 1024 ** 2;
export const fileLimits = {
    Attachment: 50,
    PublicationImage: 10,
    Avatar: 5,
} as const;
export const linkLimits = { attachments: 20, images: 50 } as const;
export const imageAccept = "image/jpeg,image/png,image/webp";
const quota = 1024 * MiB;
const lifetime = 24 * 60 * 60 * 1000;
export const mediaPath = "/mock-media/";

export function fileSize(bytes: number) {
    if (bytes === 0) return "0 Б";
    if (bytes === quota) return "1 ГиБ";
    const [amount, unit] =
        bytes >= MiB
            ? [bytes / MiB, "МиБ"]
            : bytes >= 1024
              ? [bytes / 1024, "КиБ"]
              : [bytes, "Б"];
    return (
        amount.toLocaleString("ru", { maximumFractionDigits: 1 }) + " " + unit
    );
}

export function isHttpsCover(value: string) {
    try {
        return (
            value.length <= 2048 &&
            /^https:\/\//i.test(value) &&
            new URL(value).protocol === "https:"
        );
    } catch {
        return false;
    }
}

type Reservation = {
    id: string;
    source: File;
    purpose: MediaPurpose;
    expiresAt: number;
    avatarVersion: string;
};

export class UploadExpired extends Error {
    constructor() {
        super(
            "Срок загрузки истёк. Выберите файл заново, чтобы начать новую загрузку.",
        );
    }
}

function validateSource(file: File, purpose: MediaPurpose) {
    const name = file.name.trim();
    if (
        !name ||
        [...name].length > 255 ||
        /[\p{Cc}/\\]/u.test(file.name) ||
        /^([a-z]:|\.{1,2}$)/i.test(name)
    )
        throw new Error(
            "Недопустимое имя. Переименуйте файл и выберите его снова.",
        );
    if (!file.size) throw new Error("Файл пуст. Выберите другой файл.");
    if (file.size > fileLimits[purpose] * MiB)
        throw new Error(
            "Файл больше " +
                fileLimits[purpose] +
                " МиБ. Уменьшите его или выберите другой.",
        );
    if (purpose !== "Attachment" && !imageAccept.split(",").includes(file.type))
        throw new Error("Выберите изображение в формате JPEG, PNG или WebP.");
}

const imageError = () =>
    new Error(
        "Не удалось подготовить изображение. Выберите корректный JPEG, PNG или WebP без анимации.",
    );

async function checkImage(file: File) {
    const bytes = new Uint8Array(await file.arrayBuffer());
    const at = (offset: number, signature: string) =>
        [...signature].every((ch, i) => bytes[offset + i] === ch.charCodeAt(0));
    if (file.type === "image/jpeg") {
        if (bytes[0] !== 255 || bytes[1] !== 216 || bytes[2] !== 255)
            throw imageError();
    } else if (file.type === "image/webp") {
        if (
            !at(0, "RIFF") ||
            !at(8, "WEBP") ||
            (at(12, "VP8X") && bytes[20] & 2)
        )
            throw imageError();
    } else {
        if (!at(0, "\x89PNG\r\n\x1a\n")) throw imageError();
        const view = new DataView(bytes.buffer);
        for (let offset = 8; offset + 12 <= bytes.length;) {
            if (at(offset + 4, "acTL")) throw imageError();
            if (at(offset + 4, "IDAT")) break;
            offset += 12 + view.getUint32(offset);
        }
    }
}

export async function prepareMedia(source: File, purpose: MediaPurpose) {
    if (purpose === "Attachment")
        return {
            display: source.slice(0, source.size, "application/octet-stream"),
        };
    await checkImage(source);
    const image = await createImageBitmap(source).catch(() => {
        throw imageError();
    });
    try {
        const { width, height } = image;
        if (width > 16384 || height > 16384 || width * height > 40_000_000)
            throw new Error(
                "Изображение слишком большое. Уменьшите его разрешение и повторите загрузку.",
            );
        const render = (w: number, h: number, square: boolean) => {
            const canvas = document.createElement("canvas");
            canvas.width = w;
            canvas.height = h;
            const ctx = canvas.getContext("2d");
            if (!ctx) throw imageError();
            const side = Math.min(width, height);
            if (square)
                ctx.drawImage(
                    image,
                    (width - side) / 2,
                    (height - side) / 2,
                    side,
                    side,
                    0,
                    0,
                    w,
                    h,
                );
            else ctx.drawImage(image, 0, 0, w, h);
            return new Promise<Blob>((resolve, reject) => {
                canvas.toBlob(
                    (blob) => (blob ? resolve(blob) : reject(imageError())),
                    "image/png",
                );
            });
        };
        if (purpose === "Avatar") {
            const [small, display] = await Promise.all([
                render(256, 256, true),
                render(512, 512, true),
            ]);
            return { small, display };
        }
        const scale = Math.min(1, 2560 / Math.max(width, height));
        return {
            display: await render(
                Math.max(1, Math.round(width * scale)),
                Math.max(1, Math.round(height * scale)),
                false,
            ),
        };
    } finally {
        image.close();
    }
}

const demoFiles: MediaFile[] = [localId, otherId].map((ownerId, i) => {
    const content =
        i === 0
            ? "План материала\n\n1. Задача\n2. Ход работы\n3. Что получилось\n"
            : "Заметки к статье\n\nКраткие выводы и вопросы для обсуждения.\n";
    return {
        id: "demo-notes-" + i,
        ownerId,
        purpose: "Attachment",
        name: i === 0 ? "План материала.txt" : "Заметки к статье.txt",
        size: new TextEncoder().encode(content).length,
        url:
            "data:application/octet-stream;charset=utf-8," +
            encodeURIComponent(content),
    };
});

function dispose(file: MediaFile) {
    if (file.url.startsWith("blob:")) URL.revokeObjectURL(file.url);
    if (file.smallUrl) URL.revokeObjectURL(file.smallUrl);
}

function isUsed(post: Post, id: string) {
    return (
        !post.deleted &&
        (post.coverFileId === id ||
            post.inlineImageIds?.includes(id) ||
            post.attachmentIds?.includes(id))
    );
}

export function useMockMedia(
    posts: Post[],
    canUpload: (purpose: MediaPurpose) => boolean,
    authed: boolean,
    admin: boolean,
) {
    const [files, setFiles] = useState(demoFiles);
    const filesRef = useRef(files);
    const [reserved, setReserved] = useState(0);
    const reservations = useRef(new Map<string, Reservation>());
    const avatarVersion = useRef(uid());
    const permission = useRef(canUpload);
    permission.current = canUpload;
    const update = (next: MediaFile[]) => {
        filesRef.current = next;
        setFiles(next);
    };
    const allowance = (ownerId: string) => {
        const used = files
            .filter((file) => file.ownerId === ownerId)
            .reduce((total, file) => total + file.size, 0);
        const held = ownerId === localId ? reserved : 0;
        return {
            used,
            reserved: held,
            available: quota - used - held,
            limit: quota,
        };
    };
    const totalReserved = () =>
        [...reservations.current.values()].reduce(
            (total, upload) => total + upload.source.size,
            0,
        );
    const release = (id: string) => {
        if (reservations.current.delete(id)) setReserved(totalReserved());
    };
    const reserve = (source: File, purpose: MediaPurpose) => {
        if (!permission.current(purpose))
            throw new Error("Для этой загрузки недостаточно прав.");
        validateSource(source, purpose);
        for (const item of reservations.current.values())
            if (item.expiresAt <= Date.now()) release(item.id);
        const used = filesRef.current
            .filter((file) => file.ownerId === localId)
            .reduce((total, file) => total + file.size, 0);
        if (used + totalReserved() + source.size > quota)
            throw new Error(
                "Недостаточно места. Отмените незавершённую загрузку, удалите неиспользуемые файлы или выберите файл меньшего размера.",
            );
        const upload = {
            id: uid(),
            source,
            purpose,
            expiresAt: Date.now() + lifetime,
            avatarVersion: avatarVersion.current,
        };
        reservations.current.set(upload.id, upload);
        setReserved(totalReserved());
        return upload;
    };
    const checkUpload = (upload: Reservation) => {
        if (
            !reservations.current.has(upload.id) ||
            upload.expiresAt <= Date.now()
        )
            throw new UploadExpired();
        if (!permission.current(upload.purpose))
            throw new Error("Для этой загрузки недостаточно прав.");
    };
    const complete = (
        upload: Reservation,
        { display, small }: Awaited<ReturnType<typeof prepareMedia>>,
    ) => {
        checkUpload(upload);
        if (
            upload.purpose === "Avatar" &&
            upload.avatarVersion !== avatarVersion.current
        )
            throw new Error("Аватар уже изменился. Выберите файл ещё раз.");
        const file = {
            id: upload.id,
            ownerId: localId,
            name: upload.source.name.trim(),
            size: upload.source.size,
            purpose: upload.purpose,
            url: URL.createObjectURL(display),
            smallUrl: small && URL.createObjectURL(small),
        };
        if (upload.purpose === "Avatar") {
            const previous = filesRef.current.find(
                (item) => item.purpose === "Avatar",
            );
            avatarVersion.current = uid();
            if (previous) dispose(previous);
            update([
                file,
                ...filesRef.current.filter((item) => item.purpose !== "Avatar"),
            ]);
        } else update([file, ...filesRef.current]);
        release(upload.id);
        return file;
    };
    const removeAvatar = () => {
        if (!authed) throw new Error("Войдите, чтобы изменить аватар.");
        const previous = filesRef.current.find(
            (item) => item.purpose === "Avatar",
        );
        if (!previous) return;
        avatarVersion.current = uid();
        dispose(previous);
        update(filesRef.current.filter((item) => item.id !== previous.id));
    };
    const remove = (fileId: string) => {
        const file = filesRef.current.find(
            (item) => item.id === fileId && item.purpose !== "Avatar",
        );
        if (!file || !authed || (file.ownerId !== localId && !admin))
            throw new Error("Файл недоступен.");
        if (posts.some((post) => isUsed(post, fileId)))
            throw new Error(
                "Файл используется в сохранённой статье. Сначала уберите его из статьи и сохраните изменения.",
            );
        dispose(file);
        update(filesRef.current.filter((item) => item.id !== fileId));
    };
    const validateLinks = (post: Post) => {
        const validate = (
            ids: string[],
            purpose: LibraryPurpose,
            limit: number,
        ) => {
            if (ids.length > limit)
                throw new Error("Слишком много файлов в статье.");
            if (
                ids.some(
                    (id) =>
                        !filesRef.current.some(
                            (file) =>
                                file.id === id &&
                                file.ownerId === post.author.id &&
                                file.purpose === purpose,
                        ),
                )
            )
                throw new Error(
                    "Один из файлов больше недоступен. Уберите его из статьи и выберите другой.",
                );
        };
        validate(
            post.attachmentIds ?? [],
            "Attachment",
            linkLimits.attachments,
        );
        validate(
            post.inlineImageIds ?? [],
            "PublicationImage",
            linkLimits.images,
        );
        if (post.coverFileId) {
            if (post.coverUrl)
                throw new Error("Выберите один источник обложки.");
            validate([post.coverFileId], "PublicationImage", 1);
        }
    };
    const linked = (
        post: Post,
        ids: string[] | undefined,
        purpose: LibraryPurpose,
    ) =>
        (ids ?? []).flatMap((id) => {
            const file = files.find(
                (item) =>
                    item.id === id &&
                    item.purpose === purpose &&
                    item.ownerId === post.author.id,
            );
            return file ? [file] : [];
        });
    return {
        reserve,
        release,
        checkUpload,
        complete,
        remove,
        removeAvatar,
        validateLinks,
        allowance,
        library: (ownerId: string) =>
            authed && (ownerId === localId || admin)
                ? files.filter(
                      (file) =>
                          file.ownerId === ownerId && file.purpose !== "Avatar",
                  )
                : [],
        avatar: files.find((item) => item.purpose === "Avatar") ?? null,
        attachments: (post: Post) =>
            linked(post, post.attachmentIds, "Attachment"),
        images: (post: Post) =>
            linked(post, post.inlineImageIds, "PublicationImage"),
        cover: (post: Post) =>
            post.coverFileId
                ? (linked(post, [post.coverFileId], "PublicationImage")[0]
                      ?.url ?? "")
                : post.coverUrl,
        reset: () => {
            filesRef.current.forEach(dispose);
            reservations.current.clear();
            setReserved(0);
            avatarVersion.current = uid();
            update(demoFiles);
        },
    };
}
