import { api } from '../api/client'
import { useLoad } from './useLoad'

/** Customers and warehouses from the server, with helpers to turn an id into a name. */
export function useReferenceData() {
  const load = useLoad(async () => {
    const [customers, warehouses] = await Promise.all([api.listCustomers(), api.listWarehouses()])
    return { customers, warehouses }
  }, [])

  const customerName = (id: number) => load.data?.customers.find((c) => c.customerId === id)?.name ?? `Customer ${id}`
  const warehouseName = (id: number) => load.data?.warehouses.find((w) => w.warehouseId === id)?.name ?? `Warehouse ${id}`

  return { ...load, customerName, warehouseName }
}
