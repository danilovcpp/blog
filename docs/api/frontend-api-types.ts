/**
 * Blog Engine API 1.0.0. Wire DTOs, not runtime validators.
 * Source: openapi-v1.yaml.
 * See frontend-api-spec.md for authorization, validation and transitions.
 * Int64 is a JSON number on the wire; use a lossless parser if full precision is needed.
 */
export type UUID = string;
export type UtcDateTime = string;
export type Int64 = number;
export type LocalRole = "Reader" | "Author" | "Moderator" | "Admin";
export type PublicationState = "Draft" | "Published";
export type CommentState = "Visible" | "Hidden" | "Deleted";
export type PublicationSort = "recent" | "popular";

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  hasNextPage: boolean;
}

export interface PageQuery {
  page?: number;
  pageSize?: number;
}

export interface FeedQuery extends PageQuery {
  sort?: PublicationSort;
  categoryIds?: UUID[];
  tagIds?: UUID[];
}

export interface WorkspaceQuery extends PageQuery {
  state?: PublicationState;
}

export interface CommentQuery extends PageQuery {
  parentId?: UUID;
}

export interface ModerationQuery extends PageQuery {
  publicationId?: UUID;
  state?: CommentState;
}

export interface AuthorRef {
  id: UUID;
  avatar: AvatarRef | null;
}

export interface Me {
  id: UUID;
  roles: LocalRole[];
  avatar: AvatarRef | null;
  avatarVersion: UUID;
}

export interface AvatarRef {
  fileId: UUID;
  smallUrl: string;
  largeUrl: string;
}

export interface AvatarState {
  fileId: UUID | null;
  version: UUID;
  avatar: AvatarRef | null;
}

export type MediaPurpose = "Attachment" | "PublicationImage" | "Avatar";
export type UploadState =
  | "AwaitingContent"
  | "ContentReceived"
  | "Processing"
  | "Completed"
  | "Rejected"
  | "Cancelled"
  | "Expired";

export interface UploadCreate {
  purpose: MediaPurpose;
  name: string;
  declaredSize: Int64;
  declaredContentType: string;
}

export interface UploadResult {
  uploadId: UUID;
  fileId: UUID;
  purpose: MediaPurpose;
  completedAt: UtcDateTime;
  appliedAvatarVersion: UUID | null;
}

export interface Upload extends UploadCreate {
  id: UUID;
  fileId: UUID;
  createdAt: UtcDateTime;
  expiresAt: UtcDateTime;
  state: UploadState;
  result: UploadResult | null;
  errorCode: string | null;
}

export interface Allowance {
  limit: Int64;
  used: Int64;
  reserved: Int64;
  available: Int64;
}

export interface FileMetadata {
  id: UUID;
  ownerId: UUID;
  purpose: "Attachment" | "PublicationImage";
  name: string;
  size: Int64;
  contentType: string;
  createdAt: UtcDateTime;
  state: "Ready";
  originalUrl: string | null;
  displayUrl: string | null;
}

export type FilePage = Page<FileMetadata>;

export interface FileQuery extends PageQuery {
  ownerId?: UUID;
}

export interface PublicationImageRef {
  fileId: UUID;
  url: string;
  width: number;
  height: number;
}

export interface PublicationAttachmentRef {
  fileId: UUID;
  name: string;
  size: Int64;
  url: string;
}

export interface Taxonomy {
  id: UUID;
  name: string;
  slug: string;
  version: UUID;
}

export interface TaxonomyCreate {
  name: string;
  slug: string;
}

export interface TaxonomyRename {
  name: string;
}

export interface Counters {
  likeCount: Int64;
  viewCount: Int64;
  isLikedByCurrentUser: boolean;
}

export interface PublicationCard extends Counters {
  id: UUID;
  title: string;
  slug: string;
  description: string | null;
  coverUrl: string | null;
  coverFileId: UUID | null;
  author: AuthorRef;
  createdAt: UtcDateTime;
  updatedAt: UtcDateTime;
  firstPublishedAt: UtcDateTime;
  categories: Taxonomy[];
  tags: Taxonomy[];
}

export interface Publication extends PublicationCard {
  markdown: string;
  inlineImages: PublicationImageRef[];
  attachments: PublicationAttachmentRef[];
}

export interface WorkspaceCard extends Omit<PublicationCard, "firstPublishedAt"> {
  firstPublishedAt: UtcDateTime | null;
  state: PublicationState;
  version: UUID;
}

export interface WorkspacePublication extends WorkspaceCard {
  markdown: string;
  inlineImages: PublicationImageRef[];
  attachments: PublicationAttachmentRef[];
  inlineImageIds: UUID[];
  attachmentIds: UUID[];
}

export interface PublicationWrite {
  title: string;
  slug: string;
  markdown: string;
  categoryIds: UUID[];
  tagIds: UUID[];
  description?: string | null;
  coverUrl?: string | null;
  coverFileId?: UUID | null;
  inlineImageIds?: UUID[];
  attachmentIds?: UUID[];
}

export interface CommentCreate {
  content: string;
  parentId?: UUID | null;
}

export interface CommentUpdate {
  content: string;
}

export interface PublicCommentBase {
  id: UUID;
  publicationId: UUID;
  parentId: UUID | null;
  depth: number;
  createdAt: UtcDateTime;
  editedAt: UtcDateTime | null;
  hasReplies: boolean;
}

export interface VisibleComment extends PublicCommentBase {
  state: "Visible";
  content: string;
  author: AuthorRef;
}

export interface CommentPlaceholder extends PublicCommentBase {
  state: "Hidden" | "Deleted";
  content: null;
  author: null;
}

export type PublicComment = VisibleComment | CommentPlaceholder;

export interface PrivateCommentBase {
  id: UUID;
  publicationId: UUID;
  parentId: UUID | null;
  depth: number;
  author: AuthorRef;
  createdAt: UtcDateTime;
  editedAt: UtcDateTime | null;
  version: UUID;
}

export type CommentEditContext = PrivateCommentBase & (
  | { state: "Visible"; content: string }
  | { state: "Hidden"; content: string | null }
  | { state: "Deleted"; content: null }
);

export type ModerationComment = PrivateCommentBase & (
  | { state: "Visible" | "Hidden"; content: string }
  | { state: "Deleted"; content: null }
);

export interface ViewResult {
  counted: boolean;
  viewCount: Int64;
  anonymousVisitorId: string | null;
}

export interface Health {
  status: "live" | "ready";
}

export type ProblemCode =
  | "validation_failed"
  | "invalid_request"
  | "authentication_required"
  | "invalid_token"
  | "forbidden"
  | "resource_not_found"
  | "concurrency_conflict"
  | "slug_conflict"
  | "taxonomy_in_use"
  | "invalid_state"
  | "request_too_large"
  | "unsupported_media_type"
  | "method_not_allowed"
  | "not_acceptable"
  | "internal_error"
  | "dependency_unavailable"
  | "identity_keys_unavailable"
  | "quota_exceeded"
  | "idempotency_conflict"
  | "content_conflict"
  | "upload_in_progress"
  | "file_in_use";

export interface Problem {
  type: string;
  title: string;
  status: number;
  code: ProblemCode;
  traceId: string;
  detail?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

export type PublicationPage = Page<PublicationCard>;
export type WorkspacePage = Page<WorkspaceCard>;
export type TaxonomyPage = Page<Taxonomy>;
export type CommentPage = Page<PublicComment>;
export type ModerationPage = Page<ModerationComment>;
