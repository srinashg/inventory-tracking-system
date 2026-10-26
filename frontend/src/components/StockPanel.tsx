import type { InventoryItem, Product } from '../types'
import { Card } from './Card'

interface StockPanelProps {
  warehouseName: string
  /** The products to show stock for. */
  productIds: number[]
  products: Product[]
  /** Stock rows for the warehouse, from the server. */
  stock: InventoryItem[]
}

/** On hand, reserved and available for the chosen products, like "Stock in Dallas" in the blueprint's sample screen. */
export function StockPanel({ warehouseName, productIds, products, stock }: StockPanelProps) {
  return (
    <Card title={`Stock in ${warehouseName}`}>
      {productIds.length === 0 && <p className="hint">Choose a product to see how much is in stock.</p>}
      {productIds.map((productId) => {
        const row = stock.find((item) => item.productId === productId)
        const product = products.find((p) => p.productId === productId)

        if (!row) {
          return (
            <p key={productId} className="hint">
              No stock recorded for {product?.sku ?? `product ${productId}`} in {warehouseName}.
            </p>
          )
        }

        return (
          <div key={productId} className="stock-product">
            <h3>{row.name}</h3>
            <dl className="stock-numbers">
              <dt>On hand</dt>
              <dd>{row.onHand}</dd>
              <dt>Reserved</dt>
              <dd>{row.reserved}</dd>
              <dt>Available</dt>
              <dd className="available">{row.available}</dd>
            </dl>
          </div>
        )
      })}
    </Card>
  )
}
