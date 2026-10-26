import { useState, type FormEvent } from 'react'
import type { Product } from '../types'
import { FormField } from './FormField'
import { productOptions } from './productOptions'

export interface AdjustValues {
  productId: number
  quantityChange: number
  reason: string
}

interface AdjustStockFormProps {
  products: Product[]
  pending: boolean
  fieldErrors: Record<string, string>
  onSubmit: (values: AdjustValues) => void
}

/** Corrects on-hand stock by a signed amount (for example -2 for damaged units), with a reason. */
export function AdjustStockForm({ products, pending, fieldErrors, onSubmit }: AdjustStockFormProps) {
  const [productId, setProductId] = useState('')
  const [change, setChange] = useState('')
  const [reason, setReason] = useState('')
  const [problems, setProblems] = useState<Record<string, string>>({})

  function handleSubmit(event: FormEvent) {
    event.preventDefault()

    const found: Record<string, string> = {}
    if (!productId) found.productId = 'Choose a product.'
    if (!/^-?\d+$/.test(change.trim())) found.quantityChange = 'Enter a whole number, such as -2 or 5.'
    setProblems(found)
    if (Object.keys(found).length > 0) return

    onSubmit({ productId: Number(productId), quantityChange: Number(change), reason })
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
      <FormField label="Change" value={change} onChange={setChange} error={problems.quantityChange ?? fieldErrors.quantityChange} />
      <FormField label="Reason" value={reason} onChange={setReason} error={fieldErrors.reason} />
      <button type="submit" className="button" disabled={pending}>
        {pending ? 'Saving…' : 'Adjust stock'}
      </button>
    </form>
  )
}
