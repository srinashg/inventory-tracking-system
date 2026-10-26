import { useState, type FormEvent } from 'react'
import { FormField } from './FormField'

interface OpenOrderFormProps {
  pending: boolean
  /** A message from the server, such as "Order 999 was not found.", shown by the page. */
  onOpen: (orderId: number) => void
}

/** Opens an order by its number. There is no "list all orders" endpoint, so this is how older orders are found. */
export function OpenOrderForm({ pending, onOpen }: OpenOrderFormProps) {
  const [value, setValue] = useState('')
  const [problem, setProblem] = useState<string>()

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!/^\d+$/.test(value.trim()) || Number(value) < 1) {
      setProblem('Enter the order number as a whole number, for example 1001.')
      return
    }
    setProblem(undefined)
    onOpen(Number(value))
  }

  return (
    <form onSubmit={handleSubmit} noValidate className="inline-form">
      <FormField label="Order number" value={value} onChange={setValue} error={problem} />
      <button type="submit" className="button" disabled={pending}>
        {pending ? 'Opening…' : 'Open order'}
      </button>
    </form>
  )
}
