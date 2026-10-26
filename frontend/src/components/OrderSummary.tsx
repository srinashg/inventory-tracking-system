import type { ReactNode } from 'react'
import { formatDateTime, formatMoney } from '../lib/format'
import type { Order } from '../types'
import { Card } from './Card'
import { StatusBadge } from './StatusBadge'

interface OrderSummaryProps {
  order: Order
  customerName: string
  warehouseName: string
  /** Buttons for what can be done with this order. */
  children?: ReactNode
}

/** Everything about one order as the server reports it, with room for action buttons. */
export function OrderSummary({ order, customerName, warehouseName, children }: OrderSummaryProps) {
  return (
    <Card title={`Order #${order.orderId}`}>
      <div className="order-header">
        <StatusBadge status={order.status} />
        <dl className="facts">
          <div>
            <dt>Customer</dt>
            <dd>{customerName}</dd>
          </div>
          <div>
            <dt>Warehouse</dt>
            <dd>{warehouseName}</dd>
          </div>
          <div>
            <dt>Created</dt>
            <dd>{formatDateTime(order.createdAt)}</dd>
          </div>
        </dl>
      </div>

      <table>
        <thead>
          <tr>
            <th>SKU</th>
            <th>Product</th>
            <th className="num">Qty</th>
            <th className="num">Unit price</th>
          </tr>
        </thead>
        <tbody>
          {order.items.map((item) => (
            <tr key={item.productId}>
              <td>{item.sku}</td>
              <td>{item.name}</td>
              <td className="num">{item.quantity}</td>
              <td className="num">{formatMoney(item.unitPrice)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <td colSpan={2}>Total</td>
            <td className="num">{order.totalUnits}</td>
            <td className="num">{formatMoney(order.totalAmount)}</td>
          </tr>
        </tfoot>
      </table>

      {children && <div className="form-actions">{children}</div>}
    </Card>
  )
}
