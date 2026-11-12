# PoSeeReview.E2EAPI

HTTP contract tests against the real host, started in memory by `WebApplicationFactory`.

- **Storage** is a Testcontainers Azurite instance, so Docker must be running.
- **AI and Maps are not called**: the comic and restaurant services are replaced with fakes and
  `AiMockDelegatingHandler` intercepts any provider HTTP call that slips through.
- **Auth** uses the Dev/Test-only `X-Fake-User` / `X-Fake-Roles` headers.

Put a test here when it is about routing, status codes, auth or response shape. A test that needs
the real pipeline over real tables and blobs belongs in `PoSeeReview.Integration`.

```powershell
dotnet test tests/PoSeeReview.E2EAPI
```

Quota: 25 tests, enforced by `scripts/check-test-quota.mjs`.
