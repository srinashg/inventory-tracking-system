import { useState, type FormEvent } from 'react'
import type { Product } from '../types'
import { FormField } from './FormField'

export interface ProductFormValues {
  sku: string
  name: string
  price: number
}

interface ProductFormProps {
  /** When given, the form edits this product; otherwise it adds a new one. */
  product?: Product
  pending: boolean
  /** Per-field messages from the server, keyed by field name ("sku", "name", "price"). */
  fieldErrors: Record<string, string>
  onSubmit: (values: ProductFormValues) => void
  onCancel?: () => void
}

/** The form for adding or editing a product. The server decides what is valid; only "is it a number?" is checked here. */
export function ProductForm({ product, pending, fieldErrors, onSubmit, onCancel }: ProductFormProps) {
  const [sku, setSku] = useState(product?.sku ?? '')
  const [name, setName] = useState(product?.name ?? '')
  const [price, setPrice] = useState(product ? String(product.price) : '')
  const [priceProblem, setPriceProblem] = useState<string>()

  function handleSubmit(event: FormEvent) {
    event.preventDefault()

    const parsed = Number(price)
    if (price.trim() === '' || Number.isNaN(parsed)) {
      setPriceProblem('Enter a valid price, for example 12.50.')
      return
    }

    setPriceProblem(undefined)
    onSubmit({ sku, name, price: parsed })
  }

  return (
    <form onSubmit={handleSubmit} noValidate>
      <FormField label="SKU" value={sku} onChange={setSku} error={fieldErrors.sku} />
      <FormField label="Name" value={name} onChange={setName} error={fieldErrors.name} />
      <FormField label="Price" value={price} onChange={setPrice} error={priceProblem ?? fieldErrors.price} type="text" />
      <div className="form-actions">
        <button type="submit" className="button" disabled={pending}>
          {pending ? 'Saving…' : product ? 'Save changes' : 'Add product'}
        </button>
        {onCancel && (
          <button type="button" className="button button--secondary" onClick={onCancel} disabled={pending}>
            Cancel
          </button>
        )}
      </div>
    </form>
  )
}
