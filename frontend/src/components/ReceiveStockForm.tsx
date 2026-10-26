import { useState, type FormEvent } from 'react'
import type { Product } from '../types'
import { FormField } from './FormField'
import { productOptions } from './productOptions'

export interface ReceiveValues {
  productId: number
  quantity: number
  note?: string
}

interface ReceiveStockFormProps {
  products: Product[]
  pending: boolean
  fieldErrors: Record<string, string>
  onSubmit: (values: ReceiveValues) => void
}

/** Records new stock arriving at the selected warehouse. */
export function ReceiveStockForm({ products, pending, fieldErrors, onSubmit }: ReceiveStockFormProps) {
  const [productId, setProductId] = useState('')
  const [quantity, setQuantity] = useState('')
  const [note, setNote] = useState('')
  const [problems, setProblems] = useState<Record<string, string>>({})

  function handleSubmit(event: FormEvent) {
    event.preventDefault()

    // Only "did you fill it in, and is it a number?" is checked here. The limits are the server's to decide.
    const found: Record<string, string> = {}
    if (!productId) found.productId = 'Choose a product.'
    if (!/^-?\d+$/.test(quantity.trim())) found.quantity = 'Enter a whole number.'
    setProblems(found)
    if (Object.keys(found).length > 0) return

    onSubmit({ productId: Number(productId), quantity: Number(quantity), note: note.trim() === '' ? undefined : note })
  }

  return (
    <form onSubmit={handleSubmit} noValidate>
      <FormField
        label="Product"
        value={productId}
        onChange={setProductId}
        options={productOptions(products)}
        placeholder="Choose a product"
        error={problems.productId ?? fieldErrors.productId}
      />
      <FormField label="Quantity" value={quantity} onChange={setQuantity} error={problems.quantity ?? fieldErrors.quantity} />
      <FormField label="Note" value={note} onChange={setNote} error={fieldErrors.note} />
      <button type="submit" className="button" disabled={pending}>
        {pending ? 'Saving…' : 'Receive stock'}
      </button>
    </form>
  )
}
