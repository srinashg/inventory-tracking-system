import type { ReactNode } from 'react'

interface AlertProps {
  kind: 'error' | 'success' | 'info'
  children: ReactNode
}

/** A message box. Errors use role="alert" so screen readers announce them immediately. */
export function Alert({ kind, children }: AlertProps) {
  return (
    <div className={`alert alert--${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      {children}
    </div>
  )
}
