import { expect, it } from 'vitest'

// TEMPORARY: deliberately failing test to prove CI turns red. Never merged.
it('ci verification: this test is supposed to fail', () => {
  expect(1 + 1).toBe(3)
})
