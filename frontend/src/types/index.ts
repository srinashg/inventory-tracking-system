// Shared TypeScript types. They mirror the request and response shapes in docs/api-contract.md.

export interface Product {
  productId: number
  sku: string
  name: string
  price: number
  isActive: boolean
}

/** PATCH body: send only the fields that change. */
export interface UpdateProductRequest {
  sku?: string
  name?: string
  price?: number
  isActive?: boolean
}

export interface CreateProductRequest {
  sku: string
  name: string
  price: number
}

export interface Warehouse {
  warehouseId: number
  name: string
  location: string
}

export interface Customer {
  customerId: number
  name: string
  email: string
}

export interface InventoryItem {
  productId: number
  sku: string
  name: string
  onHand: number
  reserved: number
  available: number
}

export interface ReceiveStockRequest {
  productId: number
  warehouseId: number
  quantity: number
  note?: string
}

export interface AdjustStockRequest {
  productId: number
  warehouseId: number
  quantityChange: number
  reason: string
}

export interface StockChange {
  movementId: number
  productId: number
  warehouseId: number
  onHand: number
  reserved: number
  available: number
}

export type OrderStatus = 'Draft' | 'Reserved' | 'Picking' | 'Packed' | 'Shipped' | 'Canceled'

export interface OrderLineRequest {
  productId: number
  quantity: number
}

export interface CreateOrderRequest {
  customerId: number
  warehouseId: number
  items: OrderLineRequest[]
}

export interface OrderItem {
  productId: number
  sku: string
  name: string
  quantity: number
  unitPrice: number
}

export interface Order {
  orderId: number
  customerId: number
  warehouseId: number
  status: OrderStatus
  createdAt: string
  totalUnits: number
  totalAmount: number
  items: OrderItem[]
}

/** One row of the order list (GET /api/orders): an order without its lines. */
export interface OrderListItem {
  orderId: number
  customerId: number
  warehouseId: number
  status: OrderStatus
  createdAt: string
  totalUnits: number
  totalAmount: number
}

export interface LowStockItem {
  productId: number
  sku: string
  name: string
  warehouseId: number
  available: number
}

export interface DashboardSummary {
  totalAvailableUnits: number
  lowStockThreshold: number
  lowStockCount: number
  lowStockItems: LowStockItem[]
  openOrders: number
  shippedOrders: number
}

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

/** The error body the API returns (ProblemDetails), see api-contract.md section 1.2. */
export interface ProblemDetails {
  status: number
  title: string
  detail?: string
  errors?: Record<string, string[]>
}

/** The authorization policies the server may grant (see docs/api-contract.md section 2.1). */
export type Permission = 'ManageProducts' | 'ManageStock' | 'ManageOrders' | 'FulfillOrders'

/** The signed-in user, from POST /api/auth/login and GET /api/auth/me. */
export interface CurrentUser {
  userId: number
  email: string
  displayName: string
  roles: string[]
  permissions: Permission[]
}
