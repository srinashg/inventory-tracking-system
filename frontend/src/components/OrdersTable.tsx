import type { ReactNode } from 'react'
import type { OrderListItem } from '../types'
import { StatusBadge } from './StatusBadge'

interface OrdersTableProps {
  orders: OrderListItem[]
  customerName: (id: number) => string
  warehouseName: (id: number) => string
  /** The button(s) for each row, for example "View" or "Pick". */
  renderAction: (order: OrderListItem) => ReactNode
}

/** A table of orders with their current status. The page decides what the action column offers. */
export function OrdersTable({ orders, customerName, warehouseName, renderAction }: OrdersTableProps) {
  return (
    <table>
      <thead>
        <tr>
          <th>Order</th>
          <th>Customer</th>
          <th>Warehouse</th>
          <th className="num">Units</th>
          <th>Status</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {orders.map((order) => (
          <tr key={order.orderId}>
            <td>#{order.orderId}</td>
            <td>{customerName(order.customerId)}</td>
            <td>{warehouseName(order.warehouseId)}</td>
            <td className="num">{order.totalUnits}</td>
            <td>
              <StatusBadge status={order.status} />
            </td>
            <td className="row-actions">{renderAction(order)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
