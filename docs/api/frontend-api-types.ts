/**
 * Blog Engine API 1.0.0. Wire DTOs, not runtime validators.
 * Source: specs/001-blog-engine-mvp/contracts/openapi.yaml.
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
}

export interface Me {
  id: UUID;
  roles: LocalRole[];
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
  author: AuthorRef;
  createdAt: UtcDateTime;
  updatedAt: UtcDateTime;
  firstPublishedAt: UtcDateTime;
  categories: Taxonomy[];
  tags: Taxonomy[];
}

export interface Publication extends PublicationCard {
  markdown: string;
}

export interface WorkspaceCard extends Omit<PublicationCard, "firstPublishedAt"> {
  firstPublishedAt: UtcDateTime | null;
  state: PublicationState;
  version: UUID;
}

export interface WorkspacePublication extends WorkspaceCard {
  markdown: string;
}

export interface PublicationWrite {
  title: string;
  slug: string;
  markdown: string;
  categoryIds: UUID[];
  tagIds: UUID[];
  description?: string | null;
  coverUrl?: string | null;
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
  | "identity_keys_unavailable";

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
