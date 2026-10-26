import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { useAuth } from './authState'

/** Shows the nested pages only to a signed-in user; everyone else is sent to the login page and back afterwards. */
export function ProtectedRoute() {
  const auth = useAuth()
  const location = useLocation()

  if (auth.status === 'loading') {
    return <Loading />
  }

  if (auth.status === 'error') {
    return <LoadError message={auth.error ?? 'Something went wrong. Please try again.'} onRetry={auth.retry} />
  }

  if (auth.status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />
  }

  return <Outlet />
}
