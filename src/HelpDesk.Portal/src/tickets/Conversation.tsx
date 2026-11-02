import type { components } from '../api/schema';
import { formatDate } from './labels';

type Comment = components['schemas']['CommentResponse'];

// React escapa todo texto que se pone entre llaves: si un comentario trae "<script>", se muestra como texto y no se
// ejecuta. Por eso no hace falta sanitizar aquí (sí haría falta con dangerouslySetInnerHTML, que no se usa).
export function Conversation({ comments }: { comments: Comment[] }) {
  return (
    <ol className="conversation">
      {comments.map((comment) => (
        <li key={comment.id} className="comment">
          <div className="comment-meta">
            <strong>{comment.author}</strong>
            <time dateTime={comment.createdAt}>{formatDate(comment.createdAt)}</time>
          </div>
          <p className="comment-body">{comment.body}</p>
        </li>
      ))}
    </ol>
  );
}
