// Small builders for API-shaped test data. Tests override only the fields they care about.
import type { Customer, DashboardSummary, InventoryItem, Order, OrderListItem, Product, Warehouse } from '../types'

export const product = (over: Partial<Product> = {}): Product => ({
  productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99, isActive: true, ...over,
})

export const warehouse = (over: Partial<Warehouse> = {}): Warehouse => ({
  warehouseId: 1, name: 'Dallas', location: 'Dallas, TX', ...over,
})

export const customer = (over: Partial<Customer> = {}): Customer => ({
  customerId: 1, name: 'Amelia Reed', email: 'amelia@example.com', ...over,
})

export const inventoryItem = (over: Partial<InventoryItem> = {}): InventoryItem => ({
  productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14', onHand: 10, reserved: 0, available: 10, ...over,
})

export const order = (over: Partial<Order> = {}): Order => ({
  orderId: 1001,
  customerId: 1,
  warehouseId: 1,
  status: 'Draft',
  createdAt: '2026-10-25T18:30:00Z',
  totalUnits: 2,
  totalAmount: 2599.98,
  items: [{ productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14', quantity: 2, unitPrice: 1299.99 }],
  ...over,
})

export const orderListItem = (over: Partial<OrderListItem> = {}): OrderListItem => ({
  orderId: 1001,
  customerId: 1,
  warehouseId: 1,
  status: 'Draft',
  createdAt: '2026-10-25T18:30:00Z',
  totalUnits: 2,
  totalAmount: 2599.98,
  ...over,
})

export const summary = (over: Partial<DashboardSummary> = {}): DashboardSummary => ({
  totalAvailableUnits: 245,
  lowStockThreshold: 5,
  lowStockCount: 1,
  lowStockItems: [{ productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14', warehouseId: 1, available: 3 }],
  openOrders: 4,
  shippedOrders: 12,
  ...over,
})

export const paged = <T,>(items: T[], over: Partial<{ page: number; pageSize: number; totalCount: number }> = {}) => ({
  items, page: 1, pageSize: 50, totalCount: items.length, ...over,
})
