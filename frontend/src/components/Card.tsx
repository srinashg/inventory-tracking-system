import { useId, type ReactNode } from 'react'

interface CardProps {
  title: string
  children: ReactNode
}

/** A titled panel, like "New order" or "Stock in Dallas" in the blueprint's sample screen. */
export function Card({ title, children }: CardProps) {
  const headingId = useId()

  // aria-labelledby makes the panel a named "region", so people using screen readers (and tests) can find it by title.
  return (
    <section className="card" aria-labelledby={headingId}>
      <h2 id={headingId}>{title}</h2>
      {children}
    </section>
  )
}
