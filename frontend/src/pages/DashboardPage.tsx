import { api } from '../api/client'
import { Card } from '../components/Card'
import { EmptyState } from '../components/EmptyState'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { PageTitle } from '../components/PageTitle'
import { useLoad } from '../hooks/useLoad'

/** Stock and order totals, plus the items that are running low. All numbers come from the server. */
export function DashboardPage() {
  const { data, loading, error, reload } = useLoad(async () => {
    const [summary, warehouses] = await Promise.all([api.getDashboardSummary(), api.listWarehouses()])
    return { summary, warehouseNames: new Map(warehouses.map((w) => [w.warehouseId, w.name])) }
  }, [])

  return (
    <>
      <PageTitle title="Dashboard" subtitle="Stock and orders at a glance" />

      {loading && !data && <Loading />}
      {error && <LoadError message={error} onRetry={reload} />}

      {data && (
        <>
          <div className="stats">
            <Stat label="Units available" value={data.summary.totalAvailableUnits} />
            <Stat label="Open orders" value={data.summary.openOrders} />
            <Stat label="Shipped orders" value={data.summary.shippedOrders} />
          </div>

          <Card title="Low-stock alerts">
            <p className="hint">Items with {data.summary.lowStockThreshold} or fewer units available.</p>
            {data.summary.lowStockItems.length === 0 ? (
              <EmptyState>No items are low on stock.</EmptyState>
            ) : (
              <>
                <table>
                  <thead>
                    <tr>
                      <th>SKU</th>
                      <th>Product</th>
                      <th>Warehouse</th>
                      <th className="num">Available</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.summary.lowStockItems.map((item) => (
                      <tr key={`${item.productId}-${item.warehouseId}`}>
                        <td>{item.sku}</td>
                        <td>{item.name}</td>
                        <td>{data.warehouseNames.get(item.warehouseId) ?? `Warehouse ${item.warehouseId}`}</td>
                        <td className="num">{item.available}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {data.summary.lowStockCount > data.summary.lowStockItems.length && (
                  <p className="hint">
                    Showing {data.summary.lowStockItems.length} of {data.summary.lowStockCount} low-stock items.
                  </p>
                )}
              </>
            )}
          </Card>
        </>
      )}
    </>
  )
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="stat">
      <span className="stat__label">{label}</span>
      <span className="stat__value">{value}</span>
    </div>
  )
}
