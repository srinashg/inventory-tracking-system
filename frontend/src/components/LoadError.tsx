import { Alert } from './Alert'

interface LoadErrorProps {
  message: string
  onRetry: () => void
}

/** What a page shows when its data could not be loaded: the server's message and a retry button. */
export function LoadError({ message, onRetry }: LoadErrorProps) {
  return (
    <div>
      <Alert kind="error">{message}</Alert>
      <button type="button" className="button button--secondary" onClick={onRetry}>
        Try again
      </button>
    </div>
  )
}
