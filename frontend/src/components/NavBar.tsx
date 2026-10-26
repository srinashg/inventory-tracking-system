import { NavLink } from 'react-router-dom'
import type { CurrentUser } from '../types'
import { NavBarTitle } from './NavBarTitle'

const links = [
  { to: '/', label: 'Dashboard' },
  { to: '/products', label: 'Products' },
  { to: '/inventory', label: 'Inventory' },
  { to: '/orders', label: 'Orders' },
  { to: '/fulfillment', label: 'Fulfillment' },
]

interface NavBarProps {
  /** The signed-in user, shown at the bottom with a sign-out button. */
  user?: CurrentUser
  onSignOut?: () => void
}

/** The left-hand navigation. NavLink marks the current page with aria-current="page". */
export function NavBar({ user, onSignOut }: NavBarProps) {
  return (
    <nav className="navbar" aria-label="Main">
      <NavBarTitle />
      <ul>
        {links.map((link) => (
          <li key={link.to}>
            <NavLink to={link.to} end={link.to === '/'}>
              {link.label}
            </NavLink>
          </li>
        ))}
      </ul>
      {user && (
        <div className="navbar__user">
          <div className="navbar__name">{user.displayName}</div>
          <div className="navbar__role">{user.roles.join(', ')}</div>
          <button type="button" className="button button--small button--secondary" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      )}
    </nav>
  )
}
