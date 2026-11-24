# Places checks

Run from the repository root:

```powershell
dotnet run --project Tests/Places/Places.csproj
node Tests/Places/gateway-checks.mjs
node Tests/Reminders/Database/places-quota-checks.mjs
```

These use the production SQLite repository, view model, gateway client, Edge Function
handler and quota migration with local fixtures. They check offline labels/notes,
restart/account isolation, offline manual-place editing, rediscovery after removal,
saved-only search exclusions, legacy history compatibility, atomic offline list clearing,
cancellation races, Google storage sanitization, stable
map IDs, on-demand website/photo calls, bearer verification, Pro field masks, quota
authorization, cooldowns and daily caps. No live account or paid Google requests are used.

The quota checks use the existing `@electric-sql/pglite` dependency under
`Tests/Reminders/Database`; install that package's dependencies if not already present.

For deployment and a signed-in device smoke test, see
[the Places deployment guide](../../Deployment/Places/README.md).
