import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { Customer, Product, Warehouse } from '../types'
import { FormField } from './FormField'
import { productOptions } from './productOptions'

export interface OrderFormValues {
  customerId: number
  warehouseId: number
  items: { productId: number; quantity: number }[]
}

/** What is currently chosen, so the page can show the matching stock. */
export interface OrderSelection {
  warehouseId?: number
  productIds: number[]
}

interface Line {
  key: number // identifies the line while it is edited, so removing one does not shift the others' values
  productId: string
  quantity: string
}

interface CreateOrderFormProps {
  customers: Customer[]
  warehouses: Warehouse[]
  products: Product[]
  pending: boolean
  /** Messages from the server, keyed by field name ("customerId", "items", ...). */
  fieldErrors: Record<string, string>
  onSubmit: (values: OrderFormValues) => void
  onSelectionChange: (selection: OrderSelection) => void
}

/** The "New order" form: customer, warehouse and one or more product lines. Stock rules are checked by the server. */
export function CreateOrderForm({ customers, warehouses, products, pending, fieldErrors, onSubmit, onSelectionChange }: CreateOrderFormProps) {
  const [customerId, setCustomerId] = useState('')
  const [warehouseId, setWarehouseId] = useState('')
  const [lines, setLines] = useState<Line[]>([{ key: 1, productId: '', quantity: '' }])
  const [nextKey, setNextKey] = useState(2)
  const [problems, setProblems] = useState<Record<string, string>>({})

  // Tell the page what is selected, so it can show that warehouse's stock for those products.
  const notify = useRef(onSelectionChange)
  useEffect(() => {
    notify.current = onSelectionChange
  })
  useEffect(() => {
    notify.current({
      warehouseId: warehouseId ? Number(warehouseId) : undefined,
      productIds: lines.filter((line) => line.productId).map((line) => Number(line.productId)),
    })
  }, [warehouseId, lines])

  function updateLine(key: number, change: Partial<Line>) {
    setLines((current) => current.map((line) => (line.key === key ? { ...line, ...change } : line)))
  }

  function addLine() {
    setLines((current) => [...current, { key: nextKey, productId: '', quantity: '' }])
    setNextKey(nextKey + 1)
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()

    // Only "did you choose it, and is the quantity a number?" is checked here. Stock and limits are the server's job.
    const found: Record<string, string> = {}
    if (!customerId) found.customerId = 'Choose a customer.'
    if (!warehouseId) found.warehouseId = 'Choose a warehouse.'
    lines.forEach((line, index) => {
      if (!line.productId) found[`product${index}`] = 'Choose a product.'
      if (!/^\d+$/.test(line.quantity.trim())) found[`quantity${index}`] = 'Enter a whole number.'
    })
    setProblems(found)
    if (Object.keys(found).length > 0) return

    onSubmit({
      customerId: Number(customerId),
      warehouseId: Number(warehouseId),
      items: lines.map((line) => ({ productId: Number(line.productId), quantity: Number(line.quantity) })),
    })
  }

  const suffix = (index: number) => (index === 0 ? '' : ` ${index + 1}`)

  return (
    <form onSubmit={handleSubmit} noValidate>
      <div className="form-row">
        <FormField
          label="Customer"
          value={customerId}
          onChange={setCustomerId}
          options={customers.map((c) => ({ value: String(c.customerId), label: c.name }))}
          placeholder="Choose a customer"
          error={problems.customerId ?? fieldErrors.customerId}
        />
        <FormField
          label="Warehouse"
          value={warehouseId}
          onChange={setWarehouseId}
          options={warehouses.map((w) => ({ value: String(w.warehouseId), label: w.name }))}
          placeholder="Choose a warehouse"
          error={problems.warehouseId ?? fieldErrors.warehouseId}
        />
      </div>

      {lines.map((line, index) => (
        <div className="form-row form-row--line" key={line.key}>
          <FormField
            label={`Product${suffix(index)}`}
            value={line.productId}
            onChange={(value) => updateLine(line.key, { productId: value })}
            options={productOptions(products)}
            placeholder="Choose a product"
            error={problems[`product${index}`]}
          />
          <FormField
            label={`Quantity${suffix(index)}`}
            value={line.quantity}
            onChange={(value) => updateLine(line.key, { quantity: value })}
            error={problems[`quantity${index}`]}
          />
          {index > 0 && (
            <button
              type="button"
              className="button button--small button--secondary"
              aria-label={`Remove product ${index + 1}`}
              onClick={() => setLines((current) => current.filter((l) => l.key !== line.key))}
            >
              Remove
            </button>
          )}
        </div>
      ))}
      {fieldErrors.items && <p className="form-field__error">{fieldErrors.items}</p>}

      <div className="form-actions">
        <button type="button" className="button button--secondary" onClick={addLine}>
          Add another product
        </button>
        <button type="submit" className="button" disabled={pending}>
          {pending ? 'Creating…' : 'Create draft order'}
        </button>
      </div>
    </form>
  )
}
