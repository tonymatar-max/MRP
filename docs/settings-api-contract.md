# Settings, auth and status API contract

All routes are under `/api`. JSON, camelCase, enums as strings. Cookie auth (`cobalt_mrp_auth`, HttpOnly, SameSite=Strict).
Unauthenticated calls to anything except `/api/auth/*` return **401**. Admin-only routes return **403** for other roles.

## Auth
| Method | Route | Body | Result |
|---|---|---|---|
| GET | `/auth/status` | | `{ setupRequired: bool, authenticated: bool, user: { username, role } \| null }` |
| POST | `/auth/setup` | `{ username, password }` | Only when no users exist AND the request comes from the server itself (loopback). Creates the first **Admin** and signs in. 400 `{message}` otherwise. |
| POST | `/auth/login` | `{ username, password }` | `{ username, role }` or 401 `{message}` |
| POST | `/auth/logout` | | 204 |

Roles: `Admin` (settings, users, release to B1) and `Planner` (run MRP, view, edit forecasts, release to B1 is **Admin only**).

## Users (Admin)
| GET | `/users` | | `[{ username, role }]` |
| POST | `/users` | `{ username, password, role }` | 201 `{ username, role }`; 400 `{message}` (e.g. duplicate, password < 8 chars) |
| DELETE | `/users/{username}` | | 204; 400 if it is the last Admin or yourself |
| POST | `/users/{username}/password` | `{ password }` | 204 |

## Settings (Admin)
`GET /settings` and `PUT /settings` use the same shape. **Secrets are never returned**: `password` is always `""` in a GET and the matching `passwordSet` tells the UI whether one is stored. On PUT, an empty `password` means "keep the stored one"; a non-empty value replaces it.

```jsonc
{
  "cobaltDb": {                       // the Cobalt MRP database (runs, forecasts)
    "server": "",                     // host, host\instance or host,port (EMPTY until configured: a fresh install keeps runs in memory)
    "database": "",
    "authentication": "Windows",      // "Windows" | "Sql"
    "userName": "", "password": "", "passwordSet": false,
    "trustServerCertificate": true,
    "connectionStringOverride": ""    // advanced: if set, used instead of the fields above
  },
  "b1Db": {                           // the SAP B1 company database (read-only)
    "server": "", "database": "", "authentication": "Windows",
    "userName": "", "password": "", "passwordSet": false,
    "trustServerCertificate": true, "connectionStringOverride": ""
  },
  "serviceLayer": {                   // SAP B1 Service Layer for release / write-back
    "enabled": false,
    "baseUrl": "https://server:50000/b1s/v1",
    "companyDb": "", "userName": "", "password": "", "passwordSet": false,
    "allowInvalidCertificates": false,
    "defaultVendor": "",              // CardCode used when an item has no vendor mapping
    "itemVendors": [ { "itemCode": "", "cardCode": "" } ]
  },
  "planning": {
    "defaultSource": "mock",          // "mock" | "b1-sql"
    "horizonDays": 180, "bucketDays": 7,
    "hoursPerUnitPerDay": 8,          // capacity per resource unit (B1 stores none)
    "forecastLookbackDays": 0
  },
  "ai": {                             // optional AI assistant (OpenRouter), see docs/ai-contract.md
    "enabled": false,                 // OFF by default
    "apiKey": "", "apiKeySet": false, // secret: same rules as the passwords (never returned, blank keeps)
    "model": "anthropic/claude-opus-5",
    "maxTokens": 1024                 // clamped 64..8192
  }
}
```

| POST | `/settings/test/cobalt-db` | optional settings section | `{ ok, message }` try to connect, report server version / "database not found" / schema state |
| POST | `/settings/test/b1-db` | | `{ ok, message }` connect + count items/open orders |
| POST | `/settings/test/service-layer` | | `{ ok, message }` login then logout against the Service Layer |
| POST | `/settings/test/ai` | optional `ai` section | `{ ok, message }` one tiny chat request to OpenRouter (unsaved key/model respected, blank key = stored); readable errors for 401/402/429/timeout |
| POST | `/settings/ai/models` | optional `ai` section | `{ ok, message, models: string[] }` from OpenRouter `GET /models` |
| POST | `/settings/init-schema` | | `{ ok, message }` create/upgrade the Cobalt MRP tables in the configured Cobalt database |

The test endpoints accept the **current, possibly unsaved** values of the relevant section as the body (the section object itself, e.g. the `cobaltDb` contents, not wrapped); an empty or unconfigured body means "test what is saved". A `connectionStringOverride` comes back from GET with its password masked as `********`; sending the mask back keeps the stored password so a user can test before saving; an empty `password` falls back to the stored one. They always answer 200 with `ok` true/false (never 500).

## Status (any signed-in user)
`GET /status` → `{ version, storage: "sql" | "memory", b1Configured: bool, writebackEnabled: bool, defaultSource: "mock"|"b1-sql", horizonDays, bucketDays }`

The UI uses it to show a banner when `storage === "memory"` ("Runs are not saved: configure the Cobalt database in Settings") and to pre-fill the Run MRP dialog from `planning`.

## Release response (changed)
`POST /runs/{id}/release` → `{ released, writebackEnabled, pushedToB1, failures: [{ plannedOrderId, error }], plannedOrders }`. Admin only.
