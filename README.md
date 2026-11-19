# Domain events in EF Core 10: before or after the commit?

Companion code for the article [Domain Events in EF Core 10: Before or After the Commit?](https://codewithmukesh.com/blog/domain-events-efcore-dotnet/).

One `Order` aggregate raises `OrderPlaced`. Two handlers react: one reserves stock in the database, one sends an email. The same domain is wired three ways with a `SaveChangesInterceptor` and no mediator library:

| Mode | Interceptor | Where events run |
|------|-------------|------------------|
| Before commit | `BeforeCommitDispatchInterceptor` | In `SavingChangesAsync`, inside the same transaction |
| After commit | `AfterCommitDispatchInterceptor` | In `SavedChangesAsync`, then a second save |
| Outbox | `OutboxInterceptor` + `OutboxProcessor` | Written to `outbox_messages` in the same transaction, dispatched later |

## What the tests prove (PostgreSQL 17 via Testcontainers)

| Test | Result |
|------|--------|
| Before commit: order and reservation commit together | pass |
| Before commit: the commit fails (duplicate order number) | the email was already sent: 2 emails, 1 order |
| Before commit: the email fails | the whole order rolls back |
| After commit: the commit fails | no email |
| After commit: the email fails | `SaveChangesAsync` throws, but the order is saved and stock is never reserved |
| After commit inside `BeginTransaction` + rollback | the email went out for an order that was rolled back |
| Outbox: order and message commit together, then processed | 1 email, 1 reservation |
| Outbox: the commit fails | no message, no extra email |
| Outbox: the email fails | retried after a backoff, reservation rolled back with the failed attempt |
| Outbox: crash after the email, before the commit | the email is sent twice (at-least-once) |

## Run it

Requires the .NET 10 SDK and Docker.

```bash
dotnet test
```
