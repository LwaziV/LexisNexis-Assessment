# Order Intake & Tracking

A small end-to-end order intake system built with ASP.NET Core and Angular. The API owns validation, totals, idempotency, persistence for the running process, and status transitions. The Angular workspace provides order entry, a newest-first register, detail review, and status updates.

## Requirements

- .NET SDK 8 or later
- Node.js 20.19+ or 22.12+ and npm

The checked-in solution currently targets .NET 10 and Angular 20. Angular 20 requires Node.js 20.19+ or 22.12+.

## Run locally

Open two terminals from the repository root.

Terminal 1, start the API:

```powershell
dotnet run --project backend/OrderIntake.Api
```

The API listens at `http://localhost:5048`. Its OpenAPI document is available at `http://localhost:5048/openapi/v1.json` in Development.

Terminal 2, start the Angular app:

```powershell
cd frontend
npm install
npm start
```

Open `http://localhost:4200`. Angular proxies `/api` to the API at port 5048.

## Tests and build

From the repository root:

```powershell
dotnet test OrderIntake.slnx
dotnet build backend/OrderIntake.Api/OrderIntake.Api.csproj
```

For the frontend:

```powershell
cd frontend
npm run build
```

## API overview

- `POST /api/orders` creates an order and returns `201 Created`; an existing client reference returns the original order with `200 OK`.
- `GET /api/orders?page=1&pageSize=10` returns a newest-first page with `items`, `page`, `pageSize`, `totalCount`, and `totalPages`. Both query parameters are optional; page size defaults to 10 and is capped at 100.
- `GET /api/orders/{id}` returns order detail.
- `PATCH /api/orders/{id}/status` accepts `{ "status": "Confirmed" }`.

Order references are trimmed and compared case-insensitively. Allowed transitions are Pending to Confirmed or Cancelled, and Confirmed to Fulfilled or Cancelled. Terminal statuses cannot be changed. Invalid data returns a helpful `400`; disallowed transitions return `409`.
