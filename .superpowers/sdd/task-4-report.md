# Task 4: Nav Skeleton + Role Filtering - Implementation Report

## Summary

Successfully implemented the navigation skeleton and role-filtered sidebar for the admin dashboard. All 8 steps from the task brief completed, all tests passing, build successful.

## What Was Implemented

### Step 1: Four Placeholder Pages
Created trivial placeholder pages as specified:
- `src/pages/PerformancePage.tsx` - renders "Coming soon"
- `src/pages/StoragePage.tsx` - renders "Coming soon"
- `src/pages/TenantsPage.tsx` - renders "Coming soon"
- `src/pages/TenantAdminPage.tsx` - renders "Coming soon"

### Step 2: Sidebar Component (`src/layout/Sidebar.tsx`)
Implemented role-filtered navigation sidebar:
- Reads `user?.profile?.groups` from `useAuth()` hook (genuine `string[]` from Authentik's ID token JWT)
- Performance and Storage links rendered unconditionally
- Tenants link rendered only if `groups.includes("operators")`
- Tenant Admin link rendered only if `groups.includes("tenant-admins")`
- Role filtering uses exact `.includes()` check as specified (no additional roles or fallback logic)

### Step 3: AppLayout Component (`src/layout/AppLayout.tsx`)
Implemented the main layout wrapper:
- Header displaying user identity (email from `user.profile.email`)
- Logout button calling `signoutRedirect()`
- Embeds Sidebar component
- Outlet for nested page routes

### Step 4: Router Configuration (modified `src/router.tsx`)
Updated router to wire up the layout and pages:
- Replaced `AppLayoutPlaceholder` with real `AppLayout`
- Nested four page routes as children of the layout route:
  - `/performance` → PerformancePage
  - `/storage` → StoragePage
  - `/tenants` → TenantsPage
  - `/tenant-admin` → TenantAdminPage
- Added default redirect: `{ index: true, element: <Navigate to="/performance" replace /> }`
- Preserved AuthGate wrapper around AppLayout

### Step 5: Sidebar Tests (`src/layout/Sidebar.test.tsx`)
Implemented four comprehensive test cases with full link assertions:
1. **Operator only** (`["operators"]`) - Tenants link present, Tenant Admin absent
2. **Tenant-admin only** (`["tenant-admins"]`) - Tenant Admin link present, Tenants absent
3. **Both roles** (`["operators", "tenant-admins"]`) - Both links present
4. **Neither role** (`[]`) - Both Tenants and Tenant Admin absent

All cases verify Performance and Storage links are always rendered.

### Step 6: Router Test Extension (modified `src/router.test.tsx`)
Extended existing tests with authentication scenario:
- Added test that verifies navigating to `/` (with authenticated user) renders the redirected-to page (`/performance`, "Coming soon")
- Preserved existing unauthenticated redirect test

### Step 7: Testing & Build
- Ran `npm test`: 9 tests passed (3 files: AuthProvider, Router, Sidebar)
- Ran `npm run build`: successful, no errors

### Step 8: Commit
- Created commit: `80800a0 feat(admin-ui): nav skeleton with role-filtered Tenants/Tenant Admin links`
- Added all new files and modifications to Iverson.AdminUI/

## Test Results

```
Test Files  3 passed (3)
     Tests  9 passed (9)
    
Details:
  ✓ src/auth/AuthProvider.test.tsx (3 tests)
  ✓ src/router.test.tsx (2 tests)
  ✓ src/layout/Sidebar.test.tsx (4 tests)
```

Build output: clean, no warnings or errors.

## Files Changed/Created

**Created:**
- `Iverson.AdminUI/src/layout/AppLayout.tsx`
- `Iverson.AdminUI/src/layout/Sidebar.tsx`
- `Iverson.AdminUI/src/layout/Sidebar.test.tsx`
- `Iverson.AdminUI/src/pages/PerformancePage.tsx`
- `Iverson.AdminUI/src/pages/StoragePage.tsx`
- `Iverson.AdminUI/src/pages/TenantsPage.tsx`
- `Iverson.AdminUI/src/pages/TenantAdminPage.tsx`

**Modified:**
- `Iverson.AdminUI/src/router.tsx` (replaced placeholder, added imports, nested routes)
- `Iverson.AdminUI/src/router.test.tsx` (added authenticated redirect test)

## Self-Review Findings

### Completeness Check
- ✓ All 8 steps from brief completed
- ✓ All four Sidebar test cases implemented and passing
- ✓ Router test extended with default-redirect assertion
- ✓ All tests passing (9/9)
- ✓ Build successful

### Quality Check
- ✓ Sidebar role filtering uses exact `.includes()` logic as specified (no extras)
- ✓ Four test cases genuinely assert expected link sets (not just mocks)
- ✓ Page components are trivial placeholders (no scope creep into real content/state/fetching)
- ✓ File structure follows established patterns from Tasks 1-3
- ✓ Test patterns consistent with existing test files
- ✓ No TypeScript errors, no linting issues

### Architecture Check
- ✓ Role filtering respects exact group names: "operators" and "tenant-admins" (verified in test cases)
- ✓ AppLayout properly wraps AppLayout with AuthGate (auth boundary preserved)
- ✓ Outlet positioned for nested page routes
- ✓ Header displays user identity (email from JWT profile)
- ✓ Logout button wired to signoutRedirect()

## Issues or Concerns

None. Implementation is complete, tested, and ready for the next task.

---

## Post-Implementation: Important Test Fix (2026-07-21)

**Finding (Task 4 Review):** The test "redirects / to /performance when authenticated" in `src/router.test.tsx` only asserted that "Coming soon" text appeared. Since all four placeholder pages render identical text, this assertion could not catch a regression that changed the `Navigate to=` target from `/performance` to `/storage`, `/tenants`, or `/tenant-admin`.

**Fix Applied:**
- Updated test to be `async` and added `waitFor` import
- Added explicit pathname assertion: `expect(window.location.pathname).toBe("/performance")`
- This verifies the redirect lands specifically on `/performance`, not on any other sibling route

**RED/GREEN Validation:**
- RED: Temporarily changed router redirect to `/storage` → test failed with `expected '/storage' to be '/performance'` ✓
- GREEN: Reverted to `/performance` → all 9 tests passed ✓

**Commit:** `fd392bd Strengthen router redirect test to verify exact pathname`

**Test Output (Final):**
```
Test Files  3 passed (3)
     Tests  9 passed (9)
```

## Next Steps

Task 5 (OTel browser tracing) will consume `useAuth()` access token — this implementation provides the foundation it needs.
