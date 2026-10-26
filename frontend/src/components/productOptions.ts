import type { Product } from '../types'

/** Drop-down choices for a product list, for example "LAP-1 — Laptop Pro 14". */
export function productOptions(products: Product[]) {
  return products.map((product) => ({ value: String(product.productId), label: `${product.sku} — ${product.name}` }))
}
